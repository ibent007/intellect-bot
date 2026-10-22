using Npgsql;
namespace IntellectBot.Data;

// Последовательно применяет ещё не выполненные миграции под транзакционной блокировкой PostgreSQL.
public sealed class DatabaseMigrator(NpgsqlDataSource source)
{
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, UnifiedChatSchema.Sql),
        (2, PersonalScheduleSchema.Sql),
        (3, ActionAuditSchema.Sql),
        (4, ModelSelectionSchema.Sql),
        (5, ReminderIdentitySchema.Sql)
    ];

    public async Task ApplyAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Начальная схема также поддерживает БД, созданные до появления журнала миграций.
        await using (var initial = new NpgsqlCommand("SELECT pg_advisory_xact_lock(713421005);\n" +
            DialogueSchema.Sql + AutomationSchema.Sql + """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """, connection, transaction))
            await initial.ExecuteNonQueryAsync(ct);

        foreach (var migration in Migrations)
        {
            await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=$1)", connection, transaction);
            exists.Parameters.AddWithValue(migration.Version);
            if ((bool)(await exists.ExecuteScalarAsync(ct))!)
                continue;
            await using var apply = new NpgsqlCommand(migration.Sql, connection, transaction);
            await apply.ExecuteNonQueryAsync(ct);
            await using var record = new NpgsqlCommand("INSERT INTO schema_migrations(version) VALUES ($1)", connection, transaction);
            record.Parameters.AddWithValue(migration.Version);
            await record.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
