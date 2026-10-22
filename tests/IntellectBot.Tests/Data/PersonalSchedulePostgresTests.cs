using IntellectBot.Data;
using IntellectBot.Domain;
using IntellectBot.McpServer.Schedule;
using Xunit;
namespace IntellectBot.Tests;

public sealed partial class PostgresTests
{
    [DatabaseFact]
    public async Task AddingOneLessonDoesNotReplaceTheDayAndRepeatedAddIsIdempotent()
    {
        var store = new PersonalScheduleStore(source!);
        var first = ScheduleInput.Parse("2030-01-15 | 09:00 | 10:00 | A | ");
        var second = ScheduleInput.Parse("2030-01-15 | 11:00 | 12:00 | B | ");
        await store.AddAsync(1, first, default);
        await store.AddAsync(1, second, default);
        await store.AddAsync(1, second, default);
        var entries = await store.ReadAsync(1, new(2030, 1, 15), new(2030, 1, 15), default);
        Assert.Equal(new[] { "A", "B" }, entries.Select(x => x.Title));
    }
    [DatabaseFact]
    public async Task PersonalScheduleSurvivesNewPoolAndIsolatesOwners()
    {
        var store = new PersonalScheduleStore(source!);
        var entries = ScheduleInput.Parse("2030-01-15 | 09:00 | 10:00 | Программирование | 101");
        await store.ReplaceDaysAsync(1, entries, default);
        await store.ReplaceDaysAsync(2, ScheduleInput.Parse("2030-01-15 | 11:00 | 12:00 | Математика | 202"), default);
        await using var restarted = Npgsql.NpgsqlDataSource.Create(scopedConnection);
        var date = new DateOnly(2030, 1, 15);
        Assert.Equal("Программирование", Assert.Single(await new PersonalScheduleStore(restarted).ReadAsync(1, date, date, default)).Title);
        Assert.Equal("Математика", Assert.Single(await store.ReadAsync(2, date, date, default)).Title);
        await store.DeleteDayAsync(1, date, default);
        Assert.Empty(await store.ReadAsync(1, date, date, default));
        Assert.Single(await store.ReadAsync(2, date, date, default));
    }
    [DatabaseFact]
    public async Task ScheduleReplacementPreservesOtherDaysAndRejectsInvalidBatch()
    {
        var store = new PersonalScheduleStore(source!);
        await store.ReplaceDaysAsync(1, ScheduleInput.Parse("2030-01-15 | 09:00 | 10:00 | A | \n2030-01-16 | 09:00 | 10:00 | B | "), default);
        var replace = ScheduleInput.Parse("2030-01-15 | 11:00 | 12:00 | C | ");
        await store.ReplaceDaysAsync(1, replace, default);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceDaysAsync(1, [replace[0] with { End = replace[0].Start }], default));
        var rows = await store.ReadAsync(1, new(2030, 1, 15), new(2030, 1, 16), default);
        Assert.Equal(new[] { "C", "B" }, rows.Select(x => x.Title));
    }
    [DatabaseFact]
    public async Task MigrationConvertsLegacyModeOnceWithoutLosingDialogue()
    {
        await Store.SetModeAsync(1, "translate", default);
        await Store.SaveTurnAsync(1, "вопрос", "ответ", default);
        await Store.SetTemperatureAsync(1, 0.7, default);
        await ExecuteAgentSqlAsync("DELETE FROM schema_migrations WHERE version=1;");
        await new DatabaseMigrator(source!).ApplyAsync(default);
        await new DatabaseMigrator(source!).ApplyAsync(default);
        Assert.Equal(new UserSettings("chat", 0.7), await Store.GetOrCreateAsync(1, default));
        Assert.Equal(2, (await Store.GetHistoryAsync(1, 12, default)).Count);
    }
    [DatabaseFact]
    public async Task PersonalMcpReaderReturnsOnlyOwnedLessonsAndLocalWeek()
    {
        var state = new AgentStateStore(source!);
        await state.SetTimeZoneAsync(1, "Europe/Moscow", default);
        await state.SetTimeZoneAsync(2, "Europe/Moscow", default);
        var store = new PersonalScheduleStore(source!);
        await store.ReplaceDaysAsync(1, ScheduleInput.Parse("2026-10-12 | 09:00 | 10:00 | Моё | "), default);
        await store.ReplaceDaysAsync(2, ScheduleInput.Parse("2026-10-12 | 11:00 | 12:00 | Чужое | "), default);
        var reader = new PersonalScheduleReader(store, state, new AgentClock());
        Assert.Equal("Моё", Assert.Single((await reader.GetDayAsync(1, "2026-10-12", default)).Lessons).Title);
        Assert.Equal(7, (await reader.GetWeekAsync(1, default)).Days.Length);
        Assert.Empty((await reader.GetDayAsync(1, "2030-01-01", default)).Lessons);
    }
}
