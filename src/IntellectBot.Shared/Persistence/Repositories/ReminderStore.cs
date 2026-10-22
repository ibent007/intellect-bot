using IntellectBot.Domain;
using IntellectBot.Agent;
using Npgsql;

namespace IntellectBot.Data;

public sealed class ReminderStore(
    NpgsqlDataSource dataSource,
    TimeProvider clock) : IReminderPreparation, IReminderLifecycle
{
    private const string Columns = """
        SELECT id, text, remind_at, time_zone, status,
               created_at, expires_at, confirmed_at, result_reminder_id
        FROM agent_actions
        """;

    public async Task<PreparedReminder> PrepareAsync(
        long owner,
        long updateId,
        int proposalIndex,
        string text,
        string remindAt,
        CancellationToken ct = default)
    {
        AgentStateValidation.ValidateOwner(owner);

        if (updateId < 0 || proposalIndex is < 0 or > 1)
            throw new ArgumentException("Некорректный контекст запроса.");

        await using var connection =
            await dataSource.OpenConnectionAsync(ct);

        await using var transaction =
            await connection.BeginTransactionAsync(ct);

        string zone;

        await using (var command = Command(
            connection, transaction,
            """
            SELECT time_zone FROM bot_users
            WHERE chat_id=$1 FOR UPDATE
            """, owner))
        {
            zone = await command.ExecuteScalarAsync(ct) as string
                ?? throw new ReminderException(
                    "timezone_required",
                    "Сначала выберите свой часовой пояс.");
        }

        var draft = ReminderValidation.Parse(text, remindAt, zone);
        var now = Now();

        ReminderValidation.RequireFuture(draft, now);

        // Повтор доставки того же события сохраняет прежний результат.
        await using (var previous = Command(connection, transaction, Columns +
            " WHERE owner_chat_id=$1 AND telegram_update_id=$2 AND proposal_index=$3 FOR UPDATE",
            owner, updateId, proposalIndex))
        {
            if (await ReadActionAsync(previous, ct) is { } same)
            {
                RequireSameDraft(same.Draft, draft);
                await transaction.CommitAsync(ct);
                return same;
            }
        }
        if (await ExistingReminderAsync(connection, transaction, owner, draft, ct) is not null)
            throw new ReminderException("already_exists", "Такое напоминание уже сохранено на " +
                ReminderDisplay.InZone(draft.RemindAt, draft.TimeZone) + ". Повторная запись не нужна.");

        await using (var pending = Command(connection, transaction, Columns + "\n" + """
            WHERE owner_chat_id=$1 AND remind_at=$2 AND time_zone=$3
              AND lower(regexp_replace(btrim(text), '\s+', ' ', 'g'))=
                  lower(regexp_replace(btrim($4::text), '\s+', ' ', 'g'))
              AND status IN ('pending','confirmed') AND expires_at>$5
            ORDER BY created_at LIMIT 1 FOR UPDATE
            """, owner, draft.RemindAt, draft.TimeZone, draft.Text, now))
        {
            if (await ReadActionAsync(pending, ct) is { } waiting)
            {
                await transaction.CommitAsync(ct);
                return waiting;
            }
        }

        await using (var command = Command(
            connection, transaction,
            """
            INSERT INTO agent_actions(
                id, owner_chat_id, telegram_update_id, proposal_index,
                text, remind_at, time_zone, created_at, expires_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
            ON CONFLICT(
                owner_chat_id, telegram_update_id, proposal_index)
            DO NOTHING
            """,
            Guid.NewGuid(), owner, updateId, proposalIndex,
            draft.Text, draft.RemindAt, draft.TimeZone,
            now, now.AddMinutes(5)))
        {
            await command.ExecuteNonQueryAsync(ct);
        }

        PreparedReminder action;

        await using (var command = Command(
            connection, transaction,
            Columns + """
                
                WHERE owner_chat_id=$1
                  AND telegram_update_id=$2
                  AND proposal_index=$3
                FOR UPDATE
                """,
            owner, updateId, proposalIndex))
        {
            action = await ReadActionAsync(command, ct)
                ?? throw new InvalidDataException(
                    "Подготовленное действие отсутствует.");
        }

        RequireSameDraft(action.Draft, draft);

        await transaction.CommitAsync(ct);
        return action;
    }

    public async Task<PreparedReminder?> GetAsync(
        long owner,
        Guid actionId,
        CancellationToken ct = default)
    {
        ValidateIdentity(owner, actionId);

        await using var command = dataSource.CreateCommand(
            Columns + "\nWHERE owner_chat_id=$1 AND id=$2");

        command.Parameters.AddWithValue(owner);
        command.Parameters.AddWithValue(actionId);

        return await ReadActionAsync(command, ct);
    }

    public async Task<PreparedReminder> ConfirmAsync(
        long owner,
        Guid actionId,
        CancellationToken ct = default)
    {
        ValidateIdentity(owner, actionId);

        await using var connection =
            await dataSource.OpenConnectionAsync(ct);

        await using var transaction =
            await connection.BeginTransactionAsync(ct);

        var action = await LockAsync(
            connection, transaction, owner, actionId, ct);

        if (action.Status == "executed")
        {
            await transaction.CommitAsync(ct);
            return action;
        }

        RequireActiveState(action);

        var now = Now();

        await RejectExpiredAsync(
            connection, transaction, owner, action, now, ct);

        if (action.Status == "confirmed")
        {
            await transaction.CommitAsync(ct);
            return action;
        }

        await using (var command = Command(
            connection, transaction,
            """
            UPDATE agent_actions
            SET status='confirmed', confirmed_at=$3
            WHERE owner_chat_id=$1 AND id=$2
            """, owner, actionId, now))
        {
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        return action with
        {
            Status = "confirmed",
            ConfirmedAt = now
        };
    }

    public async Task CancelAsync(
        long owner,
        Guid actionId,
        CancellationToken ct = default)
    {
        ValidateIdentity(owner, actionId);

        await using var connection =
            await dataSource.OpenConnectionAsync(ct);

        await using var transaction =
            await connection.BeginTransactionAsync(ct);

        var action = await LockAsync(
            connection, transaction, owner, actionId, ct);

        if (action.Status == "executed")
        {
            throw new ReminderException(
                "already_executed",
                "Напоминание уже создано.");
        }

        if (action.Status is "pending" or "confirmed")
        {
            await SetTerminalAsync(
                connection, transaction,
                owner, actionId, "cancelled", ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<ReminderResult> ExecuteConfirmedAsync(
        long owner,
        Guid actionId,
        string text,
        string remindAt,
        CancellationToken ct = default)
    {
        ValidateIdentity(owner, actionId);

        await using var connection =
            await dataSource.OpenConnectionAsync(ct);

        await using var transaction =
            await connection.BeginTransactionAsync(ct);

        await using (var ownerLock = Command(connection, transaction,
            "SELECT chat_id FROM bot_users WHERE chat_id=$1 FOR UPDATE", owner))
            await ownerLock.ExecuteScalarAsync(ct);

        var action = await LockAsync(
            connection, transaction, owner, actionId, ct);

        var supplied = ReminderValidation.Parse(
            text, remindAt, action.Draft.TimeZone);

        RequireSameDraft(action.Draft, supplied);

        if (action.Status == "executed")
        {
            var existing = await ReadReminderAsync(
                connection, transaction, owner, action, false, ct);

            await transaction.CommitAsync(ct);
            return existing;
        }

        RequireActiveState(action);

        if (action.Status != "confirmed")
        {
            throw new ReminderException(
                "not_confirmed",
                "Сначала подтвердите создание напоминания.");
        }

        var now = Now();

        await RejectExpiredAsync(
            connection, transaction, owner, action, now, ct);

        bool created;

        await using (var command = Command(
            connection, transaction,
            """
            INSERT INTO agent_reminders(
                id, owner_chat_id, idempotency_key,
                text, remind_at, time_zone, identity_text)
            VALUES ($1,$2,$3,$4,$5,$6,lower(regexp_replace(btrim($4::text), '\s+', ' ', 'g')))
            ON CONFLICT DO NOTHING
            RETURNING id
            """,
            Guid.NewGuid(), owner, actionId,
            action.Draft.Text,
            action.Draft.RemindAt,
            action.Draft.TimeZone))
        {
            created = await command.ExecuteScalarAsync(ct) is Guid;
        }

        var existingId = await ExistingReminderAsync(connection, transaction, owner, action.Draft, ct)
            ?? throw new InvalidDataException("Запись напоминания отсутствует.");
        action = action with
        {
            ReminderId = existingId
        };
        var result = await ReadReminderAsync(connection, transaction, owner, action, created, ct);

        await using (var command = Command(
            connection, transaction,
            """
            UPDATE agent_actions
            SET status='executed', executed_at=$3, result_reminder_id=$4
            WHERE owner_chat_id=$1 AND id=$2
            """, owner, actionId, now, existingId))
        {
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return result;
    }

    private DateTimeOffset Now() =>
        ReminderValidation.ToDatabaseTime(clock.GetUtcNow());

    private static void ValidateIdentity(long owner, Guid actionId)
    {
        AgentStateValidation.ValidateOwner(owner);

        if (actionId == Guid.Empty)
            throw new ArgumentException("Не задано действие.");
    }

    private static void RequireSameDraft(
        ReminderDraft stored,
        ReminderDraft supplied)
    {
        if (stored != supplied)
        {
            throw new ReminderException(
                "parameters_changed",
                "Параметры изменились. Требуется новое подтверждение.");
        }
    }

    private static void RequireActiveState(PreparedReminder action)
    {
        if (action.Status is not ("pending" or "confirmed"))
        {
            throw new ReminderException(
                action.Status,
                action.Status == "cancelled"
                    ? "Создание напоминания отменено."
                    : "Срок подтверждения истёк.");
        }
    }

    private static async Task RejectExpiredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long owner,
        PreparedReminder action,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (now < action.CreatedAt ||
            (action.ConfirmedAt is { } confirmed && now < confirmed))
        {
            throw new ReminderException(
                "invalid_clock",
                "Не удалось проверить время действия.");
        }

        if (now < action.ExpiresAt && action.Draft.RemindAt > now)
            return;

        await SetTerminalAsync(
            connection, transaction, owner, action.Id, "expired", ct);

        await transaction.CommitAsync(ct);

        throw new ReminderException(
            "expired",
            "Срок подтверждения истёк или время напоминания уже прошло.");
    }

    private static async Task SetTerminalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long owner,
        Guid actionId,
        string status,
        CancellationToken ct)
    {
        await using var command = Command(
            connection, transaction,
            """
            UPDATE agent_actions
            SET status=$3, confirmed_at=NULL, executed_at=NULL
            WHERE owner_chat_id=$1 AND id=$2
            """, owner, actionId, status);

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<PreparedReminder> LockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long owner,
        Guid actionId,
        CancellationToken ct)
    {
        await using var command = Command(
            connection, transaction,
            Columns + "\nWHERE owner_chat_id=$1 AND id=$2 FOR UPDATE",
            owner, actionId);

        return await ReadActionAsync(command, ct)
            ?? throw new ReminderException(
                "not_found",
                "Действие недоступно или принадлежит другому пользователю.");
    }

    private static async Task<PreparedReminder?> ReadActionAsync(
        NpgsqlCommand command,
        CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return null;

        return new PreparedReminder(
            reader.GetGuid(0),
            new ReminderDraft(
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3)),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8));
    }

    private static async Task<ReminderResult> ReadReminderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long owner,
        PreparedReminder action,
        bool created,
        CancellationToken ct)
    {
        await using var command = Command(
            connection, transaction,
            """
            SELECT id, text, remind_at, time_zone
            FROM agent_reminders
            WHERE owner_chat_id=$1 AND (id=$2 OR idempotency_key=$3)
            ORDER BY CASE WHEN id=$2 THEN 0 ELSE 1 END LIMIT 1
            """, owner, action.ReminderId ?? Guid.Empty, action.Id);

        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            throw new InvalidDataException(
                "Запись напоминания отсутствует.");

        var stored = new ReminderDraft(
            reader.GetString(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetString(3));

        if (stored.RemindAt != action.Draft.RemindAt)
            throw new InvalidDataException("Время сохранённого напоминания отличается от подтверждённого.");
        return new ReminderResult(reader.GetGuid(0), action.Draft.RemindAt, action.Draft.TimeZone, created);
    }

    private static async Task<Guid?> ExistingReminderAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, long owner, ReminderDraft draft, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT id FROM agent_reminders
            WHERE owner_chat_id=$1 AND remind_at=$2
              AND lower(regexp_replace(btrim(text), '\s+', ' ', 'g'))=
                  lower(regexp_replace(btrim($3::text), '\s+', ' ', 'g'))
            ORDER BY identity_text NULLS LAST, created_at, id LIMIT 1
            """, owner, draft.RemindAt, draft.Text);
        return await command.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }

    private static NpgsqlCommand Command(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);

        foreach (var value in values)
            command.Parameters.AddWithValue(value);

        return command;
    }
}
