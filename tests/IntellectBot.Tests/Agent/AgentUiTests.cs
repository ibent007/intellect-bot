using IntellectBot.Agent;
using IntellectBot.Domain;
using IntellectBot.Telegram;
using Xunit;

namespace IntellectBot.Tests;

public sealed class AgentUiTests
{
    private readonly UiClock clock = new();
    private readonly MemoryAgentState state = new();
    private readonly MemoryReminderLifecycle lifecycle;
    private readonly ConfirmedGateway gateway;
    public AgentUiTests()
    {
        lifecycle = new(clock);
        gateway = new(lifecycle);
    }
    private AgentUiService Service(bool online = true) => new(state, lifecycle, () => online ? gateway : null, clock);

    [Fact]
    public async Task WhyShowsNewReplyAfterReminderInsteadOfStaleReminder()
    {
        var service = Service();
        await service.ReminderAsync(42, lifecycle.Action.Id, true, default);
        await service.RecordAsync(42, "Ответ на новый вопрос без инструментов.", default);
        var text = await service.WhyAsync(42, default);
        Assert.Contains("Ответ на новый вопрос", text);
        Assert.DoesNotContain("Напоминание", text);
        Assert.Equal(42, state.Owner);
    }
    [Fact]
    public async Task ConfirmWritesOnceAndRepeatedClickUsesStoredState()
    {
        var service = Service();
        var first = await service.ReminderAsync(42, lifecycle.Action.Id, true, CancellationToken.None);
        var repeated = await service.ReminderAsync(42, lifecycle.Action.Id, true, CancellationToken.None);
        Assert.Contains("сохранено", first.Text);
        Assert.Contains("уже сохранено", repeated.Text);
        Assert.Equal(1, gateway.Writes);
        Assert.False(first.CanRetry);
        Assert.Equal("success", Assert.Single(state.Last!.Tools).Execution);
    }

    [Fact]
    public async Task CancelDoesNotWriteThroughMcp()
    {
        var result = await Service().ReminderAsync(42, lifecycle.Action.Id, false, CancellationToken.None);
        Assert.Contains("отменено", result.Text);
        Assert.Equal(0, gateway.Writes);
        Assert.Equal(1, lifecycle.Cancellations);
    }

    [Fact]
    public async Task ExpiredConfirmationCannotWrite()
    {
        clock.Now = clock.Now.AddMinutes(5);
        var result = await Service().ReminderAsync(42, lifecycle.Action.Id, true, CancellationToken.None);
        Assert.Contains("истёк", result.Text);
        Assert.False(result.CanRetry);
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public async Task AnotherOwnerCannotReadOrConfirmAction()
    {
        var result = await Service().ReminderAsync(99, lifecycle.Action.Id, true, CancellationToken.None);
        Assert.DoesNotContain(lifecycle.Action.Draft.Text, result.Text);
        Assert.Equal(0, gateway.Writes);
        Assert.Equal(0, lifecycle.Confirmations);
        Assert.Null(state.Last);
    }

    [Fact]
    public async Task TemporaryFailureKeepsButtonsAndSupportsUserRetry()
    {
        gateway.Fail = true;
        var service = Service();
        var failed = await service.ReminderAsync(42, lifecycle.Action.Id, true, CancellationToken.None);
        Assert.True(failed.CanRetry);
        gateway.Fail = false;
        Assert.False((await service.ReminderAsync(42, lifecycle.Action.Id, true, CancellationToken.None)).CanRetry);
        Assert.Equal("executed", lifecycle.Action.Status);
    }

    [Fact]
    public async Task LostRpcResponseIsRecoveredWithoutRetryingWrite()
    {
        gateway.LoseResponse = true;
        var result = await Service().ReminderAsync(42, lifecycle.Action.Id, true, CancellationToken.None);
        Assert.Contains("сохранено", result.Text);
        Assert.False(result.CanRetry);
        Assert.Equal(1, gateway.Writes);
    }

    [Fact]
    public async Task OfflineMcpDoesNotClaimCreation()
    {
        var result = await Service(false).ReminderAsync(42, lifecycle.Action.Id, true, CancellationToken.None);
        Assert.True(result.CanRetry);
        Assert.Equal("confirmed", lifecycle.Action.Status);
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public async Task InvalidZoneDoesNotChangeExistingZone()
    {
        var result = await Service().TimeZoneAsync(42, "Not/AZone", CancellationToken.None);
        Assert.Contains("Неизвестный", result);
        Assert.Equal("Europe/Moscow", state.Zone);
    }

    [Fact]
    public async Task TimeZoneChangeDoesNotChangePreparedParameters()
    {
        var original = lifecycle.Action.Draft;
        await Service().TimeZoneAsync(42, "Asia/Tokyo", CancellationToken.None);
        Assert.Equal("Asia/Tokyo", state.Zone);
        Assert.Equal(original, lifecycle.Action.Draft);
    }

    [Fact]
    public async Task WhyDoesNotExposeRequestIdOrOwnerOrReminderText()
    {
        await Service().ReminderAsync(42, lifecycle.Action.Id, true, CancellationToken.None);
        var result = await Service().WhyAsync(42, CancellationToken.None);
        Assert.Contains("Напоминание", result);
        Assert.DoesNotContain(state.Last!.RequestId.ToString(), result);
        Assert.DoesNotContain(lifecycle.Action.Id.ToString(), result);
        Assert.DoesNotContain(lifecycle.Action.Draft.Text, result);
    }

    [Fact]
    public async Task WhyBeforeFirstRequestExplainsMissingAudit() =>
        Assert.Contains("Пока нет", await Service().WhyAsync(42, CancellationToken.None));

    [Fact]
    public async Task OrdinaryReplyUpdatesWhyWithoutToolCalls()
    {
        var store = new MemoryStore();
        await store.SetModeAsync(42, "translate", CancellationToken.None);
        var dialogue = new DialogueService(store, new FakeLlm { Text = "Hello" }, Fixtures.Settings.Llm,
            auditState: state, auditClock: clock);
        await dialogue.ReplyToUpdateAsync(42, 900, "Привет", CancellationToken.None);
        Assert.Contains("Ответ сформирован моделью", await Service().WhyAsync(42, CancellationToken.None));
        Assert.Empty(state.Last!.Tools);
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public void ToolsUsesDiscoveredDescriptionsAndWriteAccess()
    {
        var result = Service().ToolsText();
        Assert.Contains("get_weather — чтение данных", result);
        Assert.Contains("add_reminder — запись после подтверждения", result);
        Assert.Contains("Ресурс: расписание текущей недели.", result);
        Assert.DoesNotContain("probe_echo", result);
    }

    [Fact]
    public void PreparedMessageShowsExactParametersAndBoundedCallback()
    {
        var text = ReminderView.PreparedText(lifecycle.Action);
        Assert.Contains(lifecycle.Action.Draft.Text, text);
        Assert.Contains("12.10.2026 18:00", text);
        Assert.DoesNotContain("+03:00", text);
        Assert.DoesNotContain("18:00:00", text);
        Assert.Contains("Europe/Moscow", text);
        foreach (var button in ReminderView.Keyboard(lifecycle.Action.Id).Rows.SelectMany(row => row))
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(button.CallbackData) <= 64);
    }
}
