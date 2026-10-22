using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntellectBot.McpServer.Schedule;

public sealed class DemoScheduleSource
{
    private readonly DateOnly _from;
    private readonly DateOnly _through;
    private readonly Dictionary<DateOnly, Lesson[]> _days = [];
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeProvider _clock;

    public DemoScheduleSource(
        string json,
        string timeZone,
        TimeProvider clock)
    {
        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZone, out _))
            throw new ArgumentException("Требуется существующая IANA-зона.");

        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        _clock = clock;

        var options = new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };

        var document = JsonSerializer.Deserialize<ScheduleDocument>(
            json, options)
            ?? throw new InvalidDataException("Расписание отсутствует.");

        _from = ParseDate(document.From);
        _through = ParseDate(document.Through);

        if (_through.DayNumber - _from.DayNumber < 13 ||
            _through.DayNumber - _from.DayNumber > 365)
        {
            throw new InvalidDataException(
                "Расписание должно охватывать от 14 до 366 дней.");
        }

        if (document.Days is null)
            throw new InvalidDataException("Не задан список дней.");

        foreach (var day in document.Days)
        {
            if (day is null || day.Lessons is null)
                throw new InvalidDataException("Некорректный день.");

            var date = ParseDate(day.Date);

            if (date < _from || date > _through ||
                _days.ContainsKey(date))
            {
                throw new InvalidDataException(
                    "Дата вне периода или повторяется.");
            }

            foreach (var lesson in day.Lessons)
            {
                if (lesson is null ||
                    string.IsNullOrWhiteSpace(lesson.Title) ||
                    lesson.Title.Length > 200 ||
                    string.IsNullOrWhiteSpace(lesson.Location) ||
                    lesson.Location.Length > 200 ||
                    !TryParseTime(lesson.Start, out var start) ||
                    !TryParseTime(lesson.End, out var end) ||
                    end <= start)
                {
                    throw new InvalidDataException(
                        "Некорректные данные занятия.");
                }
            }

            _days.Add(
                date,
                day.Lessons.OrderBy(
                    lesson => lesson.Start,
                    StringComparer.Ordinal).ToArray());
        }
    }

    public DaySchedule GetDay(string date)
    {
        var parsed = ParseDate(date);

        if (parsed < _from || parsed > _through)
        {
            throw new ScheduleUnavailableException(
                $"Расписание доступно только с {Format(_from)} " +
                $"по {Format(_through)}.");
        }

        var lessons = _days.TryGetValue(parsed, out var found)
            ? found.ToArray()
            : [];

        return new DaySchedule(
            Format(parsed),
            _timeZone.Id,
            lessons);
    }

    public WeekSchedule GetCurrentWeek()
    {
        var localNow = TimeZoneInfo.ConvertTime(
            _clock.GetUtcNow(), _timeZone);

        var today = DateOnly.FromDateTime(localNow.DateTime);
        var offset = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-offset);
        var sunday = monday.AddDays(6);

        var days = Enumerable.Range(0, 7)
            .Select(index => GetDay(Format(monday.AddDays(index))))
            .ToArray();

        return new WeekSchedule(
            Format(monday),
            Format(sunday),
            _timeZone.Id,
            days);
    }

    private static DateOnly ParseDate(string? value)
    {
        if (value is null || value.Length != 10 ||
            !DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            throw new ArgumentException(
                "Дата должна быть существующей датой в формате YYYY-MM-DD.");
        }

        return date;
    }

    private static bool TryParseTime(string? value, out TimeOnly time)
    {
        time = default;

        return value is { Length: 5 } &&
            TimeOnly.TryParseExact(
                value,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out time);
    }

    private static string Format(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
