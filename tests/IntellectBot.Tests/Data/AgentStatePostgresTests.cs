using IntellectBot.Data;
using IntellectBot.Domain;
using Npgsql;
using Xunit;

namespace IntellectBot.Tests;

public sealed partial class PostgresTests
{
    private AgentStateStore AgentStore => new(source!);

    [DatabaseFact]
    public async Task AgentMigrationPreservesExistingSettingsAndHistory()
    {
        // Только изолированная схема временной тестовой БД.
        // Воспроизводим состояние до добавления функций агента.
        await ExecuteAgentSqlAsync("""
            DROP TABLE agent_reminders, agent_actions, agent_audits;
            ALTER TABLE bot_users DROP COLUMN time_zone;
            """);

        await Store.SetModeAsync(1, "review", default);
        await Store.SetTemperatureAsync(1, 0.7, default);
        await Store.SaveTurnAsync(1, "question", "answer", default);

        await new DatabaseMigrator(source!).ApplyAsync(default);
        await new DatabaseMigrator(source!).ApplyAsync(default);

        Assert.Equal(
            new UserSettings("review", 0.7),
            await Store.GetOrCreateAsync(1, default));

        Assert.Equal(
            new[] { "question", "answer" },
            (await Store.GetHistoryAsync(1, 12, default))
                .Select(message => message.Content));

        Assert.Null(await AgentStore.GetTimeZoneAsync(1, default));

        await AgentStore.SetTimeZoneAsync(
            1, "Europe/Moscow", default);

        Assert.Equal(
            "Europe/Moscow",
            await AgentStore.GetTimeZoneAsync(1, default));
    }

    [DatabaseFact]
    public async Task TimeZoneSurvivesRestartAndPreservesDialogue()
    {
        await Store.SetModeAsync(1, "translate", default);
        await Store.SetTemperatureAsync(1, 1.0, default);
        await Store.SaveTurnAsync(1, "a", "b", default);

        await AgentStore.SetTimeZoneAsync(
            1, "Europe/Berlin", default);

        await using var restarted =
            NpgsqlDataSource.Create(scopedConnection);

        var restartedStore = new AgentStateStore(restarted);

        Assert.Equal(
            "Europe/Berlin",
            await restartedStore.GetTimeZoneAsync(1, default));

        Assert.Equal(
            new UserSettings("translate", 1.0),
            await Store.GetOrCreateAsync(1, default));

        Assert.Equal(
            2, (await Store.GetHistoryAsync(1, 12, default)).Count);

        Assert.Null(await AgentStore.GetTimeZoneAsync(2, default));
    }

    [DatabaseFact]
    public async Task InvalidTimeZoneDoesNotChangeStoredValue()
    {
        await AgentStore.SetTimeZoneAsync(
            1, "Europe/Moscow", default);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            AgentStore.SetTimeZoneAsync(1, "Unknown/Zone", default));

        Assert.Equal(
            "Europe/Moscow",
            await AgentStore.GetTimeZoneAsync(1, default));
    }

    [DatabaseFact]
    public async Task AuditIsPersistentAndIsolatedByOwner()
    {
        const string summary = "'); DROP TABLE bot_users; --";

        var audit = MakeAudit(summary);

        await AgentStore.SaveAuditAsync(1, audit, default);

        await using var restarted =
            NpgsqlDataSource.Create(scopedConnection);

        var actual = Assert.IsType<AgentAuditEntry>(
            await new AgentStateStore(restarted)
                .GetLastAuditAsync(1, default));

        Assert.Equal(audit.RequestId, actual.RequestId);
        Assert.Equal(audit.CreatedAt, actual.CreatedAt);
        Assert.Equal(audit.Action, actual.Action);
        Assert.Equal(audit.Reason, actual.Reason);

        var tool = Assert.Single(actual.Tools);

        Assert.Equal("get_weather", tool.Name);
        Assert.Equal(summary, tool.ArgumentsSummary);
        Assert.Equal("passed", tool.Validation);
        Assert.Equal("success", tool.Execution);

        Assert.Null(await AgentStore.GetLastAuditAsync(2, default));
        Assert.True(await new DatabaseProbe(source!).PingAsync(default));
    }

    [DatabaseFact]
    public async Task AuditUpdateDoesNotCreateDuplicateRequest()
    {
        var audit = MakeAudit("Город: Берлин");

        await AgentStore.SaveAuditAsync(1, audit, default);
        await AgentStore.SaveAuditAsync(
            1, audit with
            {
                Reason = "Обновлённый результат."
            }, default);

        await using var command = source!.CreateCommand("""
            SELECT count(*) FROM agent_audits
            WHERE owner_chat_id=$1 AND request_id=$2
            """);

        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(audit.RequestId);

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);

        var actual = Assert.IsType<AgentAuditEntry>(
            await AgentStore.GetLastAuditAsync(1, default));

        Assert.Equal("Обновлённый результат.", actual.Reason);
    }

    [DatabaseFact]
    public async Task LastAuditUsesRequestTime()
    {
        var first = MakeAudit("Первый запрос");
        var second = MakeAudit("Второй запрос") with
        {
            CreatedAt = first.CreatedAt.AddMinutes(1)
        };

        await AgentStore.SaveAuditAsync(1, second, default);
        await AgentStore.SaveAuditAsync(1, first, default);

        var actual = Assert.IsType<AgentAuditEntry>(
            await AgentStore.GetLastAuditAsync(1, default));

        Assert.Equal(second.RequestId, actual.RequestId);
    }

    [DatabaseFact]
    public async Task PreparedActionRejectsDuplicateTelegramUpdate()
    {
        await Store.GetOrCreateAsync(1, default);
        await InsertAgentActionAsync(1, 100, TimeSpan.FromMinutes(4));

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertAgentActionAsync(1, 100, TimeSpan.FromMinutes(4)));

        Assert.Equal("23505", error.SqlState);
    }

    [DatabaseFact]
    public async Task PreparedActionRejectsExpiryOverFiveMinutes()
    {
        await Store.GetOrCreateAsync(1, default);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertAgentActionAsync(1, 100, TimeSpan.FromMinutes(6)));

        Assert.Equal("23514", error.SqlState);
    }

    [DatabaseFact]
    public async Task ReminderCannotReferenceAnotherOwnersAction()
    {
        await Store.GetOrCreateAsync(1, default);
        await Store.GetOrCreateAsync(2, default);

        var actionId = await InsertAgentActionAsync(
            1, 100, TimeSpan.FromMinutes(4));

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAgentSqlAsync("""
                INSERT INTO agent_reminders(
                    id, owner_chat_id, idempotency_key,
                    text, remind_at, time_zone)
                VALUES (
                    $1, $2, $3, 'Задание',
                    '2026-10-11T15:00:00Z', 'Europe/Moscow'
                )
                """, Guid.NewGuid(), 2L, actionId));

        Assert.Equal("23503", error.SqlState);
    }

    [DatabaseFact]
    public async Task DeletingOwnerCascadesOnlyTheirAgentData()
    {
        await AgentStore.SetTimeZoneAsync(
            1, "Europe/Moscow", default);

        await AgentStore.SaveAuditAsync(
            1, MakeAudit("Первый пользователь"), default);

        await AgentStore.SaveAuditAsync(
            2, MakeAudit("Второй пользователь"), default);

        await InsertAgentActionAsync(
            1, 100, TimeSpan.FromMinutes(4));

        await ExecuteAgentSqlAsync(
            "DELETE FROM bot_users WHERE chat_id=$1", 1L);

        Assert.Null(await AgentStore.GetLastAuditAsync(1, default));
        Assert.NotNull(await AgentStore.GetLastAuditAsync(2, default));

        await using var command = source!.CreateCommand(
            "SELECT count(*) FROM agent_actions WHERE owner_chat_id=$1");

        command.Parameters.AddWithValue(1L);

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    private static AgentAuditEntry MakeAudit(string summary) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-10-11T12:00:00Z"),
            "call_tool",
            [
                new ToolAuditEntry(
                    "get_weather", summary, "passed", "success")
            ],
            "Нужны актуальные погодные данные.");

    private async Task<Guid> InsertAgentActionAsync(
        long owner,
        long updateId,
        TimeSpan validity)
    {
        var id = Guid.NewGuid();
        var created = DateTimeOffset.Parse("2026-10-11T12:00:00Z");

        await ExecuteAgentSqlAsync("""
            INSERT INTO agent_actions(
                id, owner_chat_id, telegram_update_id,
                text, remind_at, time_zone, created_at, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            """,
            id,
            owner,
            updateId,
            "Проверить задание",
            created.AddHours(2),
            "Europe/Moscow",
            created,
            created.Add(validity));

        return id;
    }

    private async Task ExecuteAgentSqlAsync(
        string sql,
        params object[] values)
    {
        await using var command = source!.CreateCommand(sql);

        foreach (var value in values)
            command.Parameters.AddWithValue(value);

        await command.ExecuteNonQueryAsync();
    }
}
