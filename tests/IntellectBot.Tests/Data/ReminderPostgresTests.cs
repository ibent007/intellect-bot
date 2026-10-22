using IntellectBot.Data;
using IntellectBot.Domain;
using Npgsql;
using Xunit;

namespace IntellectBot.Tests;

public sealed partial class PostgresTests
{
    private const string ReminderWhen = "2026-10-12T18:00:00+03:00";
    private const string ReminderText = "Проверить код";

    private static ReminderClock NewReminderClock() =>
        new(DateTimeOffset.Parse("2026-10-12T12:00:00Z"));

    [DatabaseFact]
    public async Task ReminderPreparationSurvivesRestart()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);

        var clock = NewReminderClock();
        var store = new ReminderStore(source!, clock);

        var action = await store.PrepareAsync(
            1, 200, 0, ReminderText, ReminderWhen);

        Assert.Equal("pending", action.Status);
        Assert.Null(action.ConfirmedAt);
        Assert.Equal(TimeSpan.FromMinutes(5),
            action.ExpiresAt - action.CreatedAt);

        await using var restarted =
            NpgsqlDataSource.Create(scopedConnection);

        var restored = Assert.IsType<PreparedReminder>(
            await new ReminderStore(restarted, clock)
                .GetAsync(1, action.Id));

        Assert.Equal(action.Id, restored.Id);
        Assert.Equal(action.Draft, restored.Draft);
        Assert.Equal(action.ExpiresAt, restored.ExpiresAt);
        Assert.Equal(0L, await ReminderRowCountAsync());
    }

    [DatabaseFact]
    public async Task ConcurrentPreparationCreatesOneAction()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);

        var store = new ReminderStore(source!, NewReminderClock());
        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<PreparedReminder> Prepare()
        {
            await start.Task;
            return await store.PrepareAsync(
                1, 200, 0, ReminderText, ReminderWhen);
        }

        var first = Prepare();
        var second = Prepare();
        start.SetResult(true);

        var actions = await Task.WhenAll(first, second);

        Assert.Equal(actions[0].Id, actions[1].Id);
        Assert.Equal(1L, await PreparedRowCountAsync());
        Assert.Equal(0L, await ReminderRowCountAsync());
    }

    [DatabaseFact]
    public async Task ConcurrentConfirmationsCreateOneReminder()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);

        var store = new ReminderStore(source!, NewReminderClock());
        var action = await store.PrepareAsync(
            1, 200, 0, ReminderText, ReminderWhen);

        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<ReminderResult> ConfirmAndExecute()
        {
            await start.Task;
            await store.ConfirmAsync(1, action.Id);

            return await store.ExecuteConfirmedAsync(
                1, action.Id, ReminderText, ReminderWhen);
        }

        var first = ConfirmAndExecute();
        var second = ConfirmAndExecute();
        start.SetResult(true);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Single(results, result => result.Created);
        Assert.Equal(1L, await PreparedRowCountAsync());
        Assert.Equal(1L, await ReminderRowCountAsync());

        var final = Assert.IsType<PreparedReminder>(
            await store.GetAsync(1, action.Id));

        Assert.Equal("executed", final.Status);
    }

    [DatabaseFact]
    public async Task UnconfirmedActionCannotCreateReminder()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);

        var store = new ReminderStore(source!, NewReminderClock());
        var action = await store.PrepareAsync(
            1, 200, 0, ReminderText, ReminderWhen);

        var error = await Assert.ThrowsAsync<ReminderException>(() =>
            store.ExecuteConfirmedAsync(
                1, action.Id, ReminderText, ReminderWhen));

        Assert.Equal("not_confirmed", error.Code);
        Assert.Equal(0L, await ReminderRowCountAsync());
    }

    [DatabaseFact]
    public async Task ExpiredConfirmationIsRejectedAndPersisted()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);

        var clock = NewReminderClock();
        var store = new ReminderStore(source!, clock);

        var action = await store.PrepareAsync(
            1, 200, 0, ReminderText, ReminderWhen);

        clock.Current = clock.Current.AddMinutes(5);

        var error = await Assert.ThrowsAsync<ReminderException>(() =>
            store.ConfirmAsync(1, action.Id));

        Assert.Equal("expired", error.Code);

        var expired = Assert.IsType<PreparedReminder>(
            await store.GetAsync(1, action.Id));

        Assert.Equal("expired", expired.Status);
        Assert.Equal(0L, await ReminderRowCountAsync());
    }

    [DatabaseFact]
    public async Task CancelledActionCannotBeConfirmed()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);

        var store = new ReminderStore(source!, NewReminderClock());
        var action = await store.PrepareAsync(
            1, 200, 0, ReminderText, ReminderWhen);

        await store.CancelAsync(1, action.Id);

        var error = await Assert.ThrowsAsync<ReminderException>(() =>
            store.ConfirmAsync(1, action.Id));

        Assert.Equal("cancelled", error.Code);
        Assert.Equal(0L, await ReminderRowCountAsync());
    }

    [DatabaseFact]
    public async Task ChangedParametersCannotReuseConfirmation()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);

        var store = new ReminderStore(source!, NewReminderClock());
        var action = await store.PrepareAsync(
            1, 200, 0, ReminderText, ReminderWhen);

        await store.ConfirmAsync(1, action.Id);

        var error = await Assert.ThrowsAsync<ReminderException>(() =>
            store.ExecuteConfirmedAsync(
                1, action.Id, "Другой текст", ReminderWhen));

        Assert.Equal("parameters_changed", error.Code);
        Assert.Equal(0L, await ReminderRowCountAsync());

        await Assert.ThrowsAsync<ReminderException>(() =>
            store.PrepareAsync(
                1, 200, 0, "Другой текст", ReminderWhen));

        var replacement = await store.PrepareAsync(
            1, 201, 0, "Другой текст", ReminderWhen);

        Assert.NotEqual(action.Id, replacement.Id);
        Assert.Equal("pending", replacement.Status);
        Assert.Null(replacement.ConfirmedAt);
    }

    [DatabaseFact]
    public async Task AnotherOwnerCannotReadConfirmCancelOrExecuteAction()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);
        await AgentStore.SetTimeZoneAsync(2, "Europe/Moscow", default);

        var store = new ReminderStore(source!, NewReminderClock());
        var action = await store.PrepareAsync(
            1, 200, 0, ReminderText, ReminderWhen);

        Assert.Null(await store.GetAsync(2, action.Id));

        await Assert.ThrowsAsync<ReminderException>(() =>
            store.ConfirmAsync(2, action.Id));

        await Assert.ThrowsAsync<ReminderException>(() =>
            store.CancelAsync(2, action.Id));

        await Assert.ThrowsAsync<ReminderException>(() =>
            store.ExecuteConfirmedAsync(
                2, action.Id, ReminderText, ReminderWhen));

        Assert.Equal(0L, await ReminderRowCountAsync());

        var unchanged = Assert.IsType<PreparedReminder>(
            await store.GetAsync(1, action.Id));

        Assert.Equal("pending", unchanged.Status);
    }

    [DatabaseFact]
    public async Task RetryAfterExecutionReturnsExistingReminder()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);

        var clock = NewReminderClock();
        var store = new ReminderStore(source!, clock);

        var action = await store.PrepareAsync(
            1, 200, 0, ReminderText, ReminderWhen);

        await store.ConfirmAsync(1, action.Id);

        var first = await store.ExecuteConfirmedAsync(
            1, action.Id, ReminderText, ReminderWhen);

        // Повтор после срока подтверждения и времени напоминания.
        clock.Current = clock.Current.AddDays(1);

        var repeated = await store.ExecuteConfirmedAsync(
            1, action.Id, ReminderText, ReminderWhen);

        Assert.True(first.Created);
        Assert.False(repeated.Created);
        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(first.RemindAt, repeated.RemindAt);
        Assert.Equal("Europe/Moscow", repeated.TimeZone);
        Assert.Equal(1L, await ReminderRowCountAsync());
    }

    [DatabaseFact]
    public async Task MissingTimeZonePreventsPreparation()
    {
        await Store.GetOrCreateAsync(1, default);

        var store = new ReminderStore(source!, NewReminderClock());

        var error = await Assert.ThrowsAsync<ReminderException>(() =>
            store.PrepareAsync(
                1, 200, 0, ReminderText, ReminderWhen));

        Assert.Equal("timezone_required", error.Code);
        Assert.Equal(0L, await PreparedRowCountAsync());
    }

    private async Task<long> PreparedRowCountAsync()
    {
        await using var command = source!.CreateCommand(
            "SELECT count(*) FROM agent_actions");

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> ReminderRowCountAsync()
    {
        await using var command = source!.CreateCommand(
            "SELECT count(*) FROM agent_reminders");

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class ReminderClock(DateTimeOffset initial)
        : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Current;
    }
}
