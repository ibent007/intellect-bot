using IntellectBot.Domain;
using Npgsql;
namespace IntellectBot.Data;

// Личное расписание. Каждый запрос ограничен владельцем; замена дней атомарна.
public sealed class PersonalScheduleStore(NpgsqlDataSource source) : IPersonalScheduleStore
{
    public async Task<IReadOnlyList<ScheduleEntry>> ReadAsync(long owner, DateOnly from, DateOnly through, CancellationToken ct)
    {
        AgentStateValidation.ValidateOwner(owner);
        if (through < from || through.DayNumber - from.DayNumber > 366)
            throw new ArgumentException("Некорректный период расписания.");
        await using var command = source.CreateCommand("""
            SELECT id,lesson_date,start_time,end_time,title,location FROM schedule_lessons
            WHERE owner_chat_id=$1 AND lesson_date BETWEEN $2 AND $3 ORDER BY lesson_date,start_time,id LIMIT 501
            """);
        command.Parameters.AddWithValue(owner);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(through);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ScheduleEntry>();
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1), reader.GetFieldValue<TimeOnly>(2),
                reader.GetFieldValue<TimeOnly>(3), reader.GetString(4), reader.GetString(5)));
        if (result.Count > 500)
            throw new ArgumentException("Слишком много занятий: выберите меньший период.");
        return result;
    }

    public Task ReplaceDaysAsync(long owner, IReadOnlyList<ScheduleEntry> entries, CancellationToken ct) => SaveAsync(owner, entries, true, ct);
    public Task AddAsync(long owner, IReadOnlyList<ScheduleEntry> entries, CancellationToken ct) => SaveAsync(owner, entries, false, ct);

    private async Task SaveAsync(long owner, IReadOnlyList<ScheduleEntry> entries, bool replace, CancellationToken ct)
    {
        AgentStateValidation.ValidateOwner(owner);
        ScheduleInput.Validate(entries);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockOwner(owner, connection, transaction, ct);
        foreach (var date in replace ? entries.Select(x => x.Date).Distinct() : Enumerable.Empty<DateOnly>())
        {
            await using var delete = new NpgsqlCommand("DELETE FROM schedule_lessons WHERE owner_chat_id=$1 AND lesson_date=$2", connection, transaction);
            delete.Parameters.AddWithValue(owner);
            delete.Parameters.AddWithValue(date);
            await delete.ExecuteNonQueryAsync(ct);
        }
        foreach (var entry in entries)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO schedule_lessons(id,owner_chat_id,lesson_date,start_time,end_time,title,location)
                VALUES ($1,$2,$3,$4,$5,$6,$7) ON CONFLICT DO NOTHING
                """, connection, transaction);
            insert.Parameters.AddWithValue(entry.Id);
            insert.Parameters.AddWithValue(owner);
            insert.Parameters.AddWithValue(entry.Date);
            insert.Parameters.AddWithValue(entry.Start);
            insert.Parameters.AddWithValue(entry.End);
            insert.Parameters.AddWithValue(entry.Title);
            insert.Parameters.AddWithValue(entry.Location);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task DeleteDayAsync(long owner, DateOnly date, CancellationToken ct)
    {
        AgentStateValidation.ValidateOwner(owner);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockOwner(owner, connection, transaction, ct);
        await using var delete = new NpgsqlCommand("DELETE FROM schedule_lessons WHERE owner_chat_id=$1 AND lesson_date=$2", connection, transaction);
        delete.Parameters.AddWithValue(owner);
        delete.Parameters.AddWithValue(date);
        await delete.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task LockOwner(long owner, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand("INSERT INTO bot_users(chat_id) VALUES ($1) ON CONFLICT DO NOTHING", connection, transaction);
        insert.Parameters.AddWithValue(owner);
        await insert.ExecuteNonQueryAsync(ct);
        await using var locked = new NpgsqlCommand("SELECT chat_id FROM bot_users WHERE chat_id=$1 FOR UPDATE", connection, transaction);
        locked.Parameters.AddWithValue(owner);
        await locked.ExecuteScalarAsync(ct);
    }
}
