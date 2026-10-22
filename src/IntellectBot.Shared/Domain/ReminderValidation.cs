using System.Globalization;
using System.Text;

namespace IntellectBot.Domain;

public static class ReminderValidation
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mmzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm'Z'",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'"
    ];

    public static ReminderDraft Parse(
        string? text,
        string? remindAt,
        string timeZone)
    {
        var normalizedText = text?.Trim() ?? "";

        if (normalizedText.EnumerateRunes().Count() is < 1 or > 500 ||
            normalizedText.Contains('\0'))
        {
            throw new ReminderException(
                "invalid_text",
                "Текст напоминания должен содержать от 1 до 500 символов.");
        }

        if (remindAt is null ||
            !DateTimeOffset.TryParseExact(
                remindAt,
                Formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var instant))
        {
            throw new ReminderException(
                "invalid_time",
                "Укажите время ISO 8601 с часовым поясом, " +
                "например 2026-10-15T18:00:00+03:00.");
        }

        string zone;

        try
        {
            zone = AgentStateValidation.ValidateTimeZone(timeZone);
        }
        catch (ArgumentException)
        {
            throw new ReminderException(
                "invalid_timezone",
                "Не задан корректный IANA-часовой пояс.");
        }

        return new ReminderDraft(
            normalizedText,
            ToDatabaseTime(instant),
            zone);
    }

    public static void RequireFuture(
        ReminderDraft draft,
        DateTimeOffset now)
    {
        if (draft.RemindAt <= ToDatabaseTime(now))
        {
            throw new ReminderException(
                "past_time",
                "Время напоминания должно быть в будущем.");
        }
    }

    public static DateTimeOffset ToDatabaseTime(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();

        return new DateTimeOffset(
            utc.Ticks - utc.Ticks % 10,
            TimeSpan.Zero);
    }
}
