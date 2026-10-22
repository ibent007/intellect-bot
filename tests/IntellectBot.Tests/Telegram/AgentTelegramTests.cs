using IntellectBot.Agent;
using IntellectBot.Domain;
using IntellectBot.Telegram;
using Xunit;

namespace IntellectBot.Tests;

public sealed class AgentTelegramTests
{
    private readonly UiClock clock = new();
    private readonly MemoryAgentState state = new();
    private readonly FakeTelegram api = new();
    private readonly MemoryReminderLifecycle lifecycle;
    private readonly ConfirmedGateway gateway;
    private readonly UpdateHandler handler;
    public AgentTelegramTests()
    {
        lifecycle = new(clock);
        gateway = new(lifecycle);
        handler = new(api, new DialogueService(new MemoryStore(), new FakeLlm(), Fixtures.Settings.Llm),
            Fixtures.Log, new AgentUiService(state, lifecycle, () => gateway, clock))
        {
            BotUsername = "mybot"
        };
    }

    [Theory]
    [InlineData("/tools", "Неизвестная команда")]
    [InlineData("/timezone", "Неизвестная команда")]
    [InlineData("/timezone@mybot Asia/Tokyo", "Неизвестная команда")]
    [InlineData("/schedule", "Неизвестная команда")]
    [InlineData("/cancel", "Неизвестная команда")]
    [InlineData("/why", "Пока нет")]
    public async Task CommandsAreHandledWithoutLlm(string command, string expected)
    {
        await handler.HandleAsync(Fixtures.Message(command), CancellationToken.None);
        Assert.Contains(expected, Assert.Single(api.Messages).Text);
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public async Task ConfirmEditsOriginalMessageAndRemovesButtons()
    {
        await handler.HandleAsync(Fixtures.Callback($"reminder:confirm:{lifecycle.Action.Id:N}"), CancellationToken.None);
        var edited = Assert.Single(api.Messages);
        Assert.True(edited.Edit);
        Assert.Empty(edited.Keyboard!.Rows);
        Assert.Equal(1, gateway.Writes);
        Assert.Single(api.Callbacks);
    }

    [Fact]
    public async Task CancelEditsOriginalMessageWithoutMcpWrite()
    {
        await handler.HandleAsync(Fixtures.Callback($"reminder:cancel:{lifecycle.Action.Id:N}"), CancellationToken.None);
        Assert.True(Assert.Single(api.Messages).Edit);
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public async Task AnotherUserCallbackIsRejectedBeforeService()
    {
        await handler.HandleAsync(Fixtures.Callback($"reminder:confirm:{lifecycle.Action.Id:N}", userId: 99), CancellationToken.None);
        Assert.Empty(api.Messages);
        Assert.True(Assert.Single(api.Callbacks).Alert);
        Assert.Equal(0, lifecycle.Confirmations);
    }

    [Fact]
    public async Task ForwardedActionIdInAnotherPrivateChatDoesNotRevealParameters()
    {
        await handler.HandleAsync(Fixtures.Callback($"reminder:confirm:{lifecycle.Action.Id:N}", chatId: 99, userId: 99), CancellationToken.None);
        Assert.DoesNotContain(lifecycle.Action.Draft.Text, Assert.Single(api.Messages).Text);
        Assert.Equal(0, gateway.Writes);
    }

    [Theory]
    [InlineData("reminder:confirm:not-a-guid")]
    [InlineData("reminder:delete:00000000000000000000000000000001")]
    [InlineData("reminder:confirm:00000000000000000000000000000000")]
    public async Task MalformedCallbackCannotExecute(string data)
    {
        await handler.HandleAsync(Fixtures.Callback(data), CancellationToken.None);
        Assert.True(Assert.Single(api.Callbacks).Alert);
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public async Task TextualAgreementIsNotButtonConfirmation()
    {
        await handler.HandleAsync(Fixtures.Message("Да, подтверждаю"), CancellationToken.None);
        Assert.Equal(0, gateway.Writes);
        Assert.Equal(0, lifecycle.Confirmations);
    }

    [Fact]
    public async Task MainMenuCentralizesManagementInSettings()
    {
        var api = new FakeTelegram();
        var handler = new UpdateHandler(api, new DialogueService(new MemoryStore(), new FakeLlm(), Fixtures.Settings.Llm), Fixtures.Log);
        await handler.HandleAsync(Fixtures.Message("/start"), default);
        var message = Assert.Single(api.Messages);
        Assert.Contains("расписание", message.Text);
        Assert.Equal(new[] { "menu:reset", "menu:help", "menu:settings" }, message.Keyboard!.Rows.SelectMany(x => x).Select(x => x.CallbackData));
    }
}
