using System.Text.Json.Serialization;

namespace IntellectBot.Telegram;

public sealed record TelegramChat(long Id, string Type);
public sealed record TelegramUser(long Id, string? Username = null);
public sealed record TelegramMessage(
    [property: JsonPropertyName("message_id")] long MessageId,
    TelegramChat Chat, string? Text = null,
    [property: JsonPropertyName("reply_markup")] InlineKeyboard? ReplyMarkup = null,
    long Date = 1);
public sealed record CallbackQuery(string Id, TelegramUser From, string? Data = null, TelegramMessage? Message = null);
public sealed record TelegramUpdate(
    [property: JsonPropertyName("update_id")] long UpdateId,
    TelegramMessage? Message = null,
    [property: JsonPropertyName("callback_query")] CallbackQuery? CallbackQuery = null);
public sealed record InlineButton(string Text, [property: JsonPropertyName("callback_data")] string CallbackData);
public sealed record InlineKeyboard([property: JsonPropertyName("inline_keyboard")] IReadOnlyList<IReadOnlyList<InlineButton>> Rows);
public sealed record OutgoingText(string Text, string? ParseMode = null);
public interface ITelegramApi
{
    Task SendAsync(long chatId, string text, InlineKeyboard? keyboard, string? parseMode, CancellationToken ct);
    Task EditAsync(long chatId, long messageId, string text, InlineKeyboard keyboard, CancellationToken ct);
    Task AnswerCallbackAsync(string id, string? text, bool alert, CancellationToken ct);
    Task TypingAsync(long chatId, CancellationToken ct);
}
public sealed class TelegramException(int code, bool notModified = false, int? retryAfter = null) : Exception($"Telegram API error {code}")
{
    public int Code { get; } = code;
    public bool NotModified { get; } = notModified;
    public int? RetryAfter { get; } = retryAfter;
}
