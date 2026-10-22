using System.Globalization;

namespace IntellectBot.Domain;

// Часовой пояс применяется до форматирования. UTC из БД не выдаётся за местное время.
public static class ReminderDisplay
{
    public static string InZone(DateTimeOffset instant, string zone) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(zone))
            .ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string Summary(ReminderDraft draft)
    {
        var local = TimeZoneInfo.ConvertTime(draft.RemindAt, TimeZoneInfo.FindSystemTimeZoneById(draft.TimeZone));
        return $"Текст: {draft.Text.EnumerateRunes().Count()} символов; время: " +
            local.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + $"; пояс: {draft.TimeZone}.";
    }
}
