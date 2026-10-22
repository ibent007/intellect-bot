using IntellectBot.Agent;
using IntellectBot.Application;
using IntellectBot.Domain;
using IntellectBot.Telegram;
using Xunit;

namespace IntellectBot.Tests;

public sealed class InterfaceFlowTests
{
    private readonly UiClock clock = new();
    private readonly MemoryAgentState state = new();
    private readonly FakeTelegram api = new();
    private readonly FakeLlm llm = new();
    private readonly InteractionState interactions;
    private readonly UpdateHandler handler;

    public InterfaceFlowTests()
    {
        interactions = new(clock);
        var lifecycle = new MemoryReminderLifecycle(clock);
        var gateway = new ConfirmedGateway(lifecycle);
        handler = new(api, new DialogueService(new MemoryStore(), llm, Fixtures.Settings.Llm), Fixtures.Log,
            new AgentUiService(state, lifecycle, () => gateway, clock), interactions: interactions)
        {
            BotUsername = "mybot"
        };
    }

    [Theory]
    [InlineData("menu", true)]
    [InlineData("settings", false)]
    public async Task CustomZoneReturnsToOriginalSettingsWithoutLlm(string prefix, bool showMenuBack)
    {
        await handler.HandleAsync(Fixtures.Callback(prefix + ":zone_custom"), default);
        api.Messages.Clear();
        await handler.HandleAsync(Fixtures.Message("Asia/Yekaterinburg"), default);
        var result = Assert.Single(api.Messages, message => message.Edit);

        Assert.Equal("Часовой пояс изменён на Asia/Yekaterinburg.", Assert.Single(api.Messages, message => !message.Edit).Text);
        Assert.True(result.Edit);
        Assert.Contains("Asia/Yekaterinburg", result.Text);
        Assert.Equal("Asia/Yekaterinburg", state.Zone);
        Assert.Equal(showMenuBack, result.Keyboard!.Rows.SelectMany(row => row).Any(button => button.CallbackData == "menu:home"));
        Assert.Empty(llm.Calls);
        Assert.Null(interactions.GetTimeZone(42));
    }

    [Fact]
    public async Task InvalidZoneKeepsInputAndCancelDoesNotChangeStoredZone()
    {
        await handler.HandleAsync(Fixtures.Callback("menu:zone_custom"), default);
        var cancel = api.Messages.Last().Keyboard!.Rows[0][0].CallbackData;
        await handler.HandleAsync(Fixtures.Message("Not/AZone"), default);
        Assert.NotNull(interactions.GetTimeZone(42));
        Assert.Equal("Europe/Moscow", state.Zone);
        await handler.HandleAsync(Fixtures.Callback(cancel), default);
        Assert.Null(interactions.GetTimeZone(42));
        Assert.Contains("Настройки", api.Messages.Last().Text);
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task OldCancelCannotCloseNewInputAndForeignButtonCannotChangeZone()
    {
        await handler.HandleAsync(Fixtures.Callback("menu:zone_custom"), default);
        var oldCancel = api.Messages.Last().Keyboard!.Rows[0][0].CallbackData;
        await handler.HandleAsync(Fixtures.Callback("menu:zone_custom"), default);
        var current = interactions.GetTimeZone(42);
        await handler.HandleAsync(Fixtures.Callback(oldCancel), default);
        Assert.Equal(current, interactions.GetTimeZone(42));
        await handler.HandleAsync(Fixtures.Callback("menu:tz:Asia/Tokyo", userId: 99), default);
        Assert.Equal("Europe/Moscow", state.Zone);
    }

    [Fact]
    public async Task ExpiredInputDoesNotReachLlmOrChangeZone()
    {
        await handler.HandleAsync(Fixtures.Callback("menu:zone_custom"), default);
        clock.Now = clock.Now.AddMinutes(6);
        await handler.HandleAsync(Fixtures.Message("Asia/Tokyo"), default);
        Assert.Contains("истекло", api.Messages.Last().Text);
        Assert.Empty(llm.Calls);
        Assert.Equal("Europe/Moscow", state.Zone);
    }

    [Theory]
    [InlineData("/timezone")]
    [InlineData("/timezone@mybot Asia/Tokyo")]
    [InlineData("/tools")]
    [InlineData("/schedule")]
    [InlineData("/cancel")]
    public async Task RemovedCommandsAreUnknownAndDoNotConsumeInput(string command)
    {
        await handler.HandleAsync(Fixtures.Callback("menu:zone_custom"), default);
        api.Messages.Clear();
        await handler.HandleAsync(Fixtures.Message(command), default);
        Assert.Equal(MenuView.UnknownText, Assert.Single(api.Messages).Text);
        Assert.NotNull(interactions.GetTimeZone(42));
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task ToolsAreOpenedFromHelpAndBackEditsSameMessage()
    {
        await handler.HandleAsync(Fixtures.Callback("menu:help"), default);
        var help = api.Messages.Last();
        Assert.DoesNotContain("/", help.Text);
        Assert.Contains(help.Keyboard!.Rows.SelectMany(row => row), button => button.CallbackData == "menu:tools");
        await handler.HandleAsync(Fixtures.Callback("menu:tools"), default);
        Assert.Contains("Доступные инструменты", api.Messages.Last().Text);
        Assert.Equal("menu:help", api.Messages.Last().Keyboard!.Rows[0][0].CallbackData);
        Assert.All(api.Messages, message => Assert.True(message.Edit));
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public void ZoneScreenHasBackAndCustomInputOnSameLastRow()
    {
        var row = MenuView.TimeZoneKeyboard(false).Rows.Last();
        Assert.Equal(new[] { "Назад", "Другой пояс" }, row.Select(button => button.Text));
        Assert.Equal(new[] { "settings:settings", "settings:zone_custom" }, row.Select(button => button.CallbackData));
    }

    [Fact]
    public async Task WhyAndReminderDatesDoNotExposeSecondsOrOffsets()
    {
        await state.SaveAuditAsync(42, new(Guid.NewGuid(), clock.Now, "call_tool",
            [new("add_reminder", "Время: 2030-01-15T18:30:00+03:00", "passed", "success")], "Готово"), default);
        await handler.HandleAsync(Fixtures.Message("/why"), default);
        var text = Assert.Single(api.Messages).Text;
        Assert.DoesNotContain("+03:00", text);
        Assert.DoesNotContain("18:30:00", text);
        var reminder = new MemoryReminderLifecycle(clock).Action;
        text = ReminderView.PreparedText(reminder);
        Assert.DoesNotContain("+03:00", text);
        Assert.DoesNotContain("18:00:00", text);
        Assert.Contains("18:00", text);
    }
}
