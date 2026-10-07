using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntellectBot.Configuration;

namespace IntellectBot.Telegram;

// Small typed adapter for the official Bot API; no Telegram SDK version coupling.
public sealed class TelegramApi(HttpClient client, string token) : ITelegramApi
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static HttpClient CreateHttpClient(AppSettings settings)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
        if (settings.TelegramProxyUrl.Length > 0)
        {
            var uri = new Uri(settings.TelegramProxyUrl);
            var builder = new UriBuilder(uri) { UserName = "", Password = "" };
            var proxy = new WebProxy(builder.Uri);
            if (uri.UserInfo.Length > 0)
            {
                var parts = uri.UserInfo.Split(':', 2);
                proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }
            handler.Proxy = proxy;
        }
        else
            handler.UseProxy = false;
        return new(handler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
    }
    private async Task<JsonElement> CallAsync(string method, object payload, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync($"https://api.telegram.org/bot{token}/{method}", payload, Json, ct);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException) { throw new TelegramException((int)response.StatusCode); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error_code", out var error) && error.TryGetInt32(out var value) ? value : (int)response.StatusCode;
                var notModified = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String && desc.GetString()!.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);
                int? retry = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("retry_after", out var retryElement) && retryElement.TryGetInt32(out var seconds) ? seconds : null;
                throw new TelegramException(code, notModified, retry);
            }
            if (!root.TryGetProperty("result", out var result))
                throw new TelegramException(502);
            return result.Clone();
        }
    }
    public async Task<TelegramUser> GetMeAsync(CancellationToken ct) => (await CallAsync("getMe", new { }, ct)).Deserialize<TelegramUser>(Json) ?? throw new TelegramException(502);
    public async Task<bool> HasWebhookAsync(CancellationToken ct) => (await CallAsync("getWebhookInfo", new { }, ct)).GetProperty("url").GetString() is { Length: > 0 };
    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken ct) => (await CallAsync("getUpdates", new { offset, timeout = 30, allowed_updates = new[] { "message", "callback_query" } }, ct)).Deserialize<List<TelegramUpdate>>(Json) ?? [];
    public async Task SendAsync(long chatId, string text, InlineKeyboard? keyboard, string? parseMode, CancellationToken ct) =>
        _ = await CallAsync("sendMessage", new
        {
            chat_id = chatId,
            text,
            reply_markup = keyboard,
            parse_mode = parseMode
        }, ct);
    public async Task EditAsync(long chatId, long messageId, string text, InlineKeyboard keyboard, CancellationToken ct)
    {
        try
        {
            await CallAsync("editMessageText", new
            {
                chat_id = chatId,
                message_id = messageId,
                text,
                reply_markup = keyboard
            }, ct);
        }
        catch (TelegramException error) when (error.NotModified) { }
    }
    public async Task AnswerCallbackAsync(string id, string? text, bool alert, CancellationToken ct) =>
        _ = await CallAsync("answerCallbackQuery", new
        {
            callback_query_id = id,
            text,
            show_alert = alert
        }, ct);
    public async Task TypingAsync(long chatId, CancellationToken ct) => _ = await CallAsync("sendChatAction", new { chat_id = chatId, action = "typing" }, ct);
}
