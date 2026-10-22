using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntellectBot.Agent;

// Явное «сегодня в 18» нельзя незаметно переносить на завтра или превращать в ответ «сегодня».
public static class ReminderRequestGuard
{
    public static string? PastTimeClarification(string question, string? zoneName, DateTimeOffset instant)
    {
        if (zoneName is null || !question.Contains("напом", StringComparison.OrdinalIgnoreCase))
            return null;
        var match = Regex.Match(question, @"сегодня\s+(?:в\s+)?(?<hour>\d{1,2})(?:[:.](?<minute>\d{2}))?(?!\d)", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["hour"].Value, out var hour) || hour > 23)
            return null;
        var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        if (minute > 59)
            return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName);
        var now = TimeZoneInfo.ConvertTime(instant, zone);
        if (new TimeOnly(hour, minute) > TimeOnly.FromDateTime(now.DateTime))
            return null;
        return $"Сегодня, {now:dd.MM.yyyy}, время {hour:00}:{minute:00} в часовом поясе {zoneName} уже прошло. " +
            $"Сейчас {now:HH:mm}. Укажите другую будущую дату и время; напоминание пока не создано.";
    }

    public static bool IsRequest(string question) => Regex.IsMatch(question,
        @"\b(?:напомни|напомните|добавь\s+напоминание|создай\s+напоминание)\b", RegexOptions.IgnoreCase);

    // Проверяем исходный запрос до подготовки кнопок подтверждения.
    public static string? ArgumentError(
        string question,
        JsonElement args,
        string? zoneName,
        DateTimeOffset now)
    {
        if (!IsRequest(question) || args.ValueKind != JsonValueKind.Object)
            return null;

        if (args.TryGetProperty("text", out var text) &&
            text.ValueKind == JsonValueKind.String)
        {
            var words = Regex.Matches(question, @"[\p{L}\p{M}\p{N}_]+")
                .Select(x => x.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (Regex.Matches(text.GetString()!, @"[\p{L}\p{M}\p{N}_]+")
                .Any(x => !words.Contains(x.Value)))
            {
                return "Текст действия искажён. Возьми слова действия дословно " +
                    "из запроса, без добавлений и замены алфавита.";
            }
        }

        var time = Regex.Match(
            question,
            @"\b(?:сегодня|завтра)\s+в\s+" +
            @"(?<h>\d{1,2}|час|два|три|четыре|пять|шесть|семь|восемь|девять|десять|одиннадцать|двенадцать)" +
            @"(?:[:.](?<m>\d{2}))?\b(?:\s+(?<period>утра|вечера|дня|ночи))?",
            RegexOptions.IgnoreCase);

        if (!time.Success ||
            zoneName is null ||
            !args.TryGetProperty("remind_at", out var date) ||
            date.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                date.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var instant))
            return null;

        var value = time.Groups["h"].Value.ToLowerInvariant();
        var hour = int.TryParse(value, out var number)
            ? number
            : Array.IndexOf(
                new[]
                {
                    "", "час", "два", "три", "четыре", "пять", "шесть",
                    "семь", "восемь", "девять", "десять", "одиннадцать", "двенадцать"
                },
                value);

        var minute = time.Groups["m"].Success
            ? int.Parse(time.Groups["m"].Value, CultureInfo.InvariantCulture)
            : 0;

        var period = time.Groups["period"].Value.ToLowerInvariant();

        if (period is "вечера" or "дня" && hour < 12)
            hour += 12;

        if (period == "ночи" && hour == 12)
            hour = 0;

        if (hour is < 0 or > 23 || minute > 59)
            return null;

        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName);
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var expectedDate = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(
            time.Value.StartsWith("завтра", StringComparison.OrdinalIgnoreCase) ? 1 : 0);

        if (local.Date == expectedDate &&
            local.Hour == hour &&
            local.Minute == minute)
            return null;

        return $"Время не соответствует запросу: требуется {expectedDate:dd.MM.yyyy} " +
            $"{hour:00}:{minute:00} в {zoneName}. " +
            "Учти смещение этого пояса на нужную дату. Если время прошло, " +
            "задай уточняющий вопрос; не переноси его самостоятельно.";
    }
}
