using System.Globalization;
using System.Text.RegularExpressions;

namespace IntellectBot.Domain;

// Только отображение дат. Значения в БД, подписях и контрактах MCP остаются точными.
public static class DisplayTime
{
    public static string InZone(DateTimeOffset instant, string zone) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(zone))
            .ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string Simplify(string text)
    {
        text = Regex.Replace(text, @"\b(?<date>\d{4}-\d{2}-\d{2})T(?=\d{2}:\d{2})", match =>
            DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) ? date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + " " : match.Value);
        return Regex.Replace(text,
            @"(?<![\d:])(?<time>(?:[01]\d|2[0-3]):[0-5]\d)(?::[0-5]\d(?:[.,]\d{1,7})?)?(?:[ \u00a0]*(?:[+-]\d{2}:\d{2}|Z))?(?![\d:])",
            "${time}");
    }
}
