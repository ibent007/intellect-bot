using IntellectBot.Data;
using IntellectBot.Domain;
using Xunit;

namespace IntellectBot.Tests;

public sealed partial class PostgresTests
{
    [DatabaseFact]
    public async Task SameReminderInNewMessageIsRejectedAfterItWasSaved()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);
        var store = new ReminderStore(source!, NewReminderClock());
        var action = await store.PrepareAsync(1, 200, 0, ReminderText, ReminderWhen);
        await store.ConfirmAsync(1, action.Id);
        var first = await store.ExecuteConfirmedAsync(1, action.Id, ReminderText, ReminderWhen);
        var error = await Assert.ThrowsAsync<ReminderException>(() => store.PrepareAsync(1, 201, 0, ReminderText, ReminderWhen));
        Assert.Equal("already_exists", error.Code);
        Assert.Contains("12.10.2026 18:00", error.Message);
        Assert.True(first.Created);
        Assert.Equal(1L, await ReminderRowCountAsync());
        Assert.Equal(1L, await PreparedRowCountAsync());
    }

    [DatabaseFact]
    public async Task ReminderIdentityIgnoresCaseAndExtraSpacesButKeepsOwnerAndTime()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);
        await AgentStore.SetTimeZoneAsync(2, "Europe/Moscow", default);
        var store = new ReminderStore(source!, NewReminderClock());
        var action = await store.PrepareAsync(1, 200, 0, "Проверить   код", ReminderWhen);
        await store.ConfirmAsync(1, action.Id);
        await store.ExecuteConfirmedAsync(1, action.Id, "Проверить   код", ReminderWhen);
        var error = await Assert.ThrowsAsync<ReminderException>(() => store.PrepareAsync(1, 201, 0, "проверить код", ReminderWhen));
        Assert.Equal("already_exists", error.Code);
        var otherOwner = await store.PrepareAsync(2, 201, 0, "проверить код", ReminderWhen);
        var otherTime = await store.PrepareAsync(1, 202, 0, "проверить код", "2026-10-12T19:00:00+03:00");
        Assert.NotEqual(action.Id, otherOwner.Id);
        Assert.NotEqual(action.Id, otherTime.Id);
    }

    [DatabaseFact]
    public async Task PendingIdenticalReminderInNewMessageReusesConfirmation()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);
        var store = new ReminderStore(source!, NewReminderClock());
        var first = await store.PrepareAsync(1, 200, 0, ReminderText, ReminderWhen);
        var second = await store.PrepareAsync(1, 201, 0, ReminderText, ReminderWhen);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.ExpiresAt, second.ExpiresAt);
        Assert.Equal(1L, await PreparedRowCountAsync());
        Assert.Equal(0L, await ReminderRowCountAsync());
    }

    [DatabaseFact]
    public async Task ConcurrentDifferentUpdatesStillPrepareOneIdenticalAction()
    {
        await AgentStore.SetTimeZoneAsync(1, "Europe/Moscow", default);
        var store = new ReminderStore(source!, NewReminderClock());
        var actions = await Task.WhenAll(store.PrepareAsync(1, 200, 0, ReminderText, ReminderWhen),
            store.PrepareAsync(1, 201, 0, ReminderText, ReminderWhen));
        Assert.Equal(actions[0].Id, actions[1].Id);
        Assert.Equal(1L, await PreparedRowCountAsync());
    }
}
