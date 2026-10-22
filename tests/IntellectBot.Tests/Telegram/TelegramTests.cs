using System.Text.Json;
using IntellectBot.Domain;
using IntellectBot.Llm;
using IntellectBot.Telegram;
using Xunit;

namespace IntellectBot.Tests;

public sealed class TelegramTests
{
    private static (UpdateHandler Handler, FakeTelegram Api, MemoryStore Store, FakeLlm Llm) Setup()
    {
        var api = new FakeTelegram();
        var store = new MemoryStore();
        var llm = new FakeLlm();
        return (new(api, new DialogueService(store, llm, Fixtures.Settings.Llm), Fixtures.Log), api, store, llm);
    }
    [Fact]
    public async Task StartHasTwoButtonsAndFullWidthSettings()
    {
        var (handler, api, _, llm) = Setup();
        await handler.HandleAsync(Fixtures.Message("/start"), default);
        var message = Assert.Single(api.Messages);
        Assert.Equal(MenuView.StartText, message.Text);
        Assert.Equal(new[] { "menu:reset", "menu:help", "menu:settings" }, message.Keyboard!.Rows.SelectMany(row => row).Select(b => b.CallbackData));
        Assert.Equal(new[] { 2, 1 }, message.Keyboard.Rows.Select(row => row.Count));
        Assert.Empty(llm.Calls);
    }
    [Theory]
    [InlineData("/settings", false)]
    [InlineData("menu:settings", true)]
    public async Task SettingsOriginControlsBackButton(string action, bool fromMenu)
    {
        var (handler, api, _, _) = Setup();
        await handler.HandleAsync(fromMenu ? Fixtures.Callback(action) : Fixtures.Message(action), default);
        var message = Assert.Single(api.Messages);
        Assert.Equal(fromMenu, message.Edit);
        Assert.Equal(fromMenu ? 4 : 3, message.Keyboard!.Rows.Count);
        Assert.Equal(new[] { "0", "✓ 0.3", "0.7", "1" }, message.Keyboard.Rows[0].Select(b => b.Text));
        Assert.Equal(new[] { "Часовой пояс", "Расписание" }, message.Keyboard.Rows[1].Select(b => b.Text));
        Assert.StartsWith("Настройки\n\nМодель: Qwen 3.8 27B\nУровень креативности: 0.3", message.Text);
        Assert.DoesNotContain("Режим:", message.Text);
    }
    [Theory]
    [InlineData("settings:review", false)]
    [InlineData("menu:review", true)]
    [InlineData("settings:translate", false)]
    [InlineData("menu:translate", true)]
    public async Task OldModeButtonsCannotChangeSettingsOrEraseHistory(string action, bool back)
    {
        var (handler, api, store, _) = Setup();
        await handler.HandleAsync(Fixtures.Message("before"), default);
        api.Messages.Clear();
        await handler.HandleAsync(Fixtures.Callback(action), default);
        Assert.Empty(api.Messages);
        Assert.Equal(2, store.History[42].Count);
        Assert.Equal("chat", store.Users[42].Mode);
        Assert.Equal(back, action.StartsWith("menu:", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("0.3", .3)]
    [InlineData("0.7", .7)]
    [InlineData("1", 1.0)]
    public async Task TemperatureButtonUpdatesSettingWithoutClearingHistory(string label, double value)
    {
        var (handler, api, store, llm) = Setup();
        await handler.HandleAsync(Fixtures.Message("before"), default);
        api.Messages.Clear();
        await handler.HandleAsync(Fixtures.Callback("settings:temperature:" + label), default);
        Assert.Equal(value, store.Users[42].Temperature);
        Assert.Equal(2, store.History[42].Count);
        Assert.Single(llm.Calls);
        Assert.Equal(3, Assert.Single(api.Messages).Keyboard!.Rows.Count);
    }
    [Theory]
    [InlineData("/reset", false)]
    [InlineData("menu:reset", true)]
    public async Task ResetOriginControlsKeyboard(string action, bool back)
    {
        var (handler, api, store, _) = Setup();
        await handler.HandleAsync(Fixtures.Message("question"), default);
        api.Messages.Clear();
        await handler.HandleAsync(back ? Fixtures.Callback(action) : Fixtures.Message(action), default);
        var message = Assert.Single(api.Messages);
        Assert.Equal(MenuView.ResetText, message.Text);
        Assert.Equal(back, message.Edit);
        Assert.Equal(back, message.Keyboard is not null);
        Assert.Empty(await store.GetHistoryAsync(42, 12, default));
    }
    [Fact]
    public async Task NonTextIsExplainedAndNeverSentToLlm()
    {
        var (handler, api, _, llm) = Setup();
        await handler.HandleAsync(Fixtures.Message(null), default);
        Assert.Equal("Я пока что принимаю только текстовые сообщения.", Assert.Single(api.Messages).Text);
        Assert.Empty(llm.Calls);
    }
    [Theory]
    [InlineData("/study", "Неизвестная команда. Используйте команду /start.")]
    [InlineData("/translate", "Неизвестная команда. Используйте команду /start.")]
    [InlineData("/review", "Неизвестная команда. Используйте команду /start.")]
    [InlineData("/temperature", "Неизвестная команда. Используйте команду /start.")]
    [InlineData("/unknown", "Неизвестная команда. Используйте команду /start.")]
    [InlineData("   ", "Напишите непустой текстовый вопрос.")]
    public async Task InvalidTextDoesNotCallLlm(string text, string expected)
    {
        var (handler, api, _, llm) = Setup();
        await handler.HandleAsync(Fixtures.Message(text), default);
        Assert.Equal(expected, Assert.Single(api.Messages).Text);
        Assert.Empty(llm.Calls);
    }
    [Theory]
    [InlineData("group")]
    [InlineData("supergroup")]
    [InlineData("channel")]
    public async Task NonPrivateChatsAreIgnored(string type)
    {
        var (handler, api, _, llm) = Setup();
        await handler.HandleAsync(Fixtures.Message("/start", type: type), default);
        Assert.Empty(api.Messages);
        Assert.Empty(llm.Calls);
    }
    [Theory]
    [InlineData("menu:settings", 7, "private", 1)]
    [InlineData("menu:settings", 42, "group", 1)]
    [InlineData("menu:settings", 42, "private", 0)]
    public async Task InvalidCallbackContextIsRejected(string data, long user, string type, long date)
    {
        var (handler, api, _, _) = Setup();
        await handler.HandleAsync(Fixtures.Callback(data, userId: user, type: type, date: date), default);
        Assert.True(Assert.Single(api.Callbacks).Alert);
        Assert.Empty(api.Messages);
    }
    [Theory]
    [InlineData("settings:home")]
    [InlineData("settings:reset")]
    [InlineData("menu:temperature:0.2")]
    [InlineData("menu:unknown")]
    public async Task ForgedButtonsAreRejected(string data)
    {
        var (handler, api, _, llm) = Setup();
        await handler.HandleAsync(Fixtures.Callback(data), default);
        Assert.Equal(("Неизвестная кнопка. Откройте /start.", true), Assert.Single(api.Callbacks));
        Assert.Empty(api.Messages);
        Assert.Empty(llm.Calls);
    }
    [Fact]
    public async Task TranslationUsesCodeAndEscapesHtml()
    {
        var (handler, api, _, llm) = Setup();
        await handler.HandleAsync(Fixtures.Callback("settings:translate"), default);
        api.Messages.Clear();
        llm.Text = "Русский → Английский\n\n<x> & 'quote'";
        await handler.HandleAsync(Fixtures.Message("translate"), default);
        var message = Assert.Single(api.Messages);
        Assert.Equal("HTML", message.ParseMode);
        Assert.Equal("Русский → Английский\n\n<code>&lt;x&gt; &amp; &#x27;quote&#x27;</code>", message.Text);
        Assert.Null(message.Keyboard);
    }
    [Theory]
    [InlineData("На какой язык перевести?")]
    [InlineData("a → b → c\n\ntext")]
    [InlineData("a → b\n\n ")]
    public void TranslationClarificationsStayPlain(string text)
    {
        Assert.Equal(new OutgoingText(text), Assert.Single(MessageFormatter.Format(new(text, Mode: "translate"))));
    }
    [Theory]
    [InlineData(4096)]
    [InlineData(10000)]
    [InlineData(4000)]
    public void LongTextPreservesAllCharacters(int count)
    {
        var text = string.Concat(Enumerable.Repeat("a🙂", count));
        var chunks = MessageFormatter.Split(text).ToArray();
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, c => { Assert.InRange(c.Length, 1, 4000); Assert.False(char.IsHighSurrogate(c[^1])); Assert.False(char.IsLowSurrogate(c[0])); });
    }
    [Fact]
    public async Task LongAnswersSentInOrderAndTypingFailureIsNonfatal()
    {
        var (handler, api, _, llm) = Setup();
        llm.Text = new string('x', 9000);
        api.FailTyping = true;
        await handler.HandleAsync(Fixtures.Message("question"), default);
        Assert.Equal(llm.Text, string.Concat(api.Messages.Select(m => m.Text)));
        Assert.Equal(3, api.Messages.Count);
        Assert.True(api.TypingCount > 0);
    }
    [Theory]
    [InlineData("timeout")]
    [InlineData("rate_limit")]
    [InlineData("invalid_response")]
    [InlineData("authorization")]
    public async Task ModelErrorBecomesExactSafeMessage(string code)
    {
        var (handler, api, store, llm) = Setup();
        llm.Error = new LlmException(code);
        await handler.HandleAsync(Fixtures.Message("question"), default);
        Assert.Equal(LlmException.MessageFor(code), Assert.Single(api.Messages).Text);
        Assert.Empty(store.History[42]);
    }
    [Fact]
    public async Task TelegramApiSerializesKeyboardAndIgnoresNotModified()
    {
        using var http = new HttpClient(new StubHttp(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(42, json.RootElement.GetProperty("chat_id").GetInt64());
            Assert.Equal("menu:home", json.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString());
            Assert.False(json.RootElement.TryGetProperty("parse_mode", out _));
            return StubHttp.Json("""{"ok":false,"error_code":400,"description":"Bad Request: message is not modified"}""");
        }));
        await new TelegramApi(http, "fake").EditAsync(42, 1, "text", MenuView.BackKeyboard(), default);
    }
    [Fact]
    public void IncomingTelegramJsonDeserializesRealPropertyNames()
    {
        const string json = """{"update_id":1,"callback_query":{"id":"a","from":{"id":42},"data":"menu:home","message":{"message_id":8,"date":1,"chat":{"id":42,"type":"private"}}}}""";
        var update = JsonSerializer.Deserialize<TelegramUpdate>(json, TelegramApi.Json)!;
        Assert.Equal(1, update.UpdateId);
        Assert.Equal(8, update.CallbackQuery!.Message!.MessageId);
    }
}
