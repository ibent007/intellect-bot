using System.Net;
using System.Text;
using IntellectBot.Configuration;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;
using IntellectBot.Telegram;

namespace IntellectBot.Tests;

public static class Fixtures
{
    public static Dictionary<string, string> Values() => new() { ["BOT_TOKEN"] = "123456:test-token", ["POSTGRES_PASSWORD"] = "test-password", ["LLM_BASE_URL"] = "https://llm.example/v1", ["LLM_API_KEY"] = "test-api-key", ["LLM_MODEL"] = "qwen/qwen3.8-27b" };
    public static AppSettings Settings => AppSettings.FromValues(Values());
    public static SafeLog Log => new(Settings);
    public static TelegramUpdate Message(string? text, long chatId = 42, string type = "private") => new(1, new(5, new(chatId, type), text));
    public static TelegramUpdate Callback(string data, long chatId = 42, long userId = 42, string type = "private", long date = 1) => new(1, CallbackQuery: new("cb", new(userId), data, new(5, new(chatId, type), Date: date)));
}
public sealed class MemoryStore : IDialogueStore
{
    public readonly Dictionary<long, UserSettings> Users = [];
    public readonly Dictionary<long, List<ChatMessage>> History = [];
    public Task<UserSettings> GetOrCreateAsync(long id, CancellationToken ct)
    {
        if (!Users.ContainsKey(id))
            Users[id] = new();
        History.TryAdd(id, []);
        return Task.FromResult(Users[id]);
    }
    public Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(long id, int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<ChatMessage>>(History.GetValueOrDefault(id, []).TakeLast(max / 2 * 2).ToArray());
    public Task SaveTurnAsync(long id, string question, string answer, CancellationToken ct)
    {
        History[id].AddRange([new("user", question), new("assistant", answer)]);
        return Task.CompletedTask;
    }
    public Task ClearHistoryAsync(long id, CancellationToken ct)
    {
        History.Remove(id);
        return Task.CompletedTask;
    }
    public async Task SetModeAsync(long id, string mode, CancellationToken ct)
    {
        await GetOrCreateAsync(id, ct);
        Users[id] = Users[id] with
        {
            Mode = mode
        };
        History[id].Clear();
    }
    public async Task SetTemperatureAsync(long id, double value, CancellationToken ct)
    {
        await GetOrCreateAsync(id, ct);
        Users[id] = Users[id] with
        {
            Temperature = value
        };
    }
}
public sealed class FakeLlm : ILlmClient
{
    public readonly List<(IReadOnlyList<ChatMessage> Messages, double Temperature)> Calls = [];
    public string Text = "Ответ";
    public Exception? Error;
    public Func<CancellationToken, Task>? BeforeResponse;
    public async Task<LlmResponse> GenerateAsync(IReadOnlyList<ChatMessage> messages, double temperature, CancellationToken ct)
    {
        Calls.Add((messages, temperature));
        if (BeforeResponse is not null)
            await BeforeResponse(ct);
        if (Error is not null)
            throw Error;
        return new(Text);
    }
}
public sealed record Sent(long ChatId, string Text, InlineKeyboard? Keyboard, string? ParseMode, bool Edit = false);
public sealed class FakeTelegram : ITelegramApi
{
    public readonly List<Sent> Messages = [];
    public readonly List<(string? Text, bool Alert)> Callbacks = [];
    public int TypingCount;
    public bool FailTyping;
    public Task SendAsync(long id, string text, InlineKeyboard? keyboard, string? mode, CancellationToken ct)
    {
        Messages.Add(new(id, text, keyboard, mode));
        return Task.CompletedTask;
    }
    public Task EditAsync(long id, long messageId, string text, InlineKeyboard keyboard, CancellationToken ct)
    {
        Messages.Add(new(id, text, keyboard, null, true));
        return Task.CompletedTask;
    }
    public Task AnswerCallbackAsync(string id, string? text, bool alert, CancellationToken ct)
    {
        Callbacks.Add((text, alert));
        return Task.CompletedTask;
    }
    public Task TypingAsync(long id, CancellationToken ct)
    {
        TypingCount++;
        if (FailTyping)
            throw new TelegramException(500);
        return Task.CompletedTask;
    }
}
public sealed class StubHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request, ct);
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
