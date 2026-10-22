using System.Globalization;
namespace IntellectBot.Domain;

// Занятие на конкретную календарную дату в часовом поясе владельца.
public sealed record ScheduleEntry(Guid Id, DateOnly Date, TimeOnly Start, TimeOnly End, string Title, string Location)
{
    public override string ToString() => nameof(ScheduleEntry);
}

public interface IPersonalScheduleStore
{
    Task<IReadOnlyList<ScheduleEntry>> ReadAsync(long owner, DateOnly from, DateOnly through, CancellationToken ct);
    Task AddAsync(long owner, IReadOnlyList<ScheduleEntry> entries, CancellationToken ct);
    Task ReplaceDaysAsync(long owner, IReadOnlyList<ScheduleEntry> entries, CancellationToken ct);
    Task DeleteDayAsync(long owner, DateOnly date, CancellationToken ct);
}

// Проверка ручного ввода до показа подтверждения и повторно перед SQL.
public static class ScheduleInput
{
    public static DateOnly Date(string text) => DateOnly.TryParseExact(text, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date :
        throw new ArgumentException("Укажите дату в формате ГГГГ-ММ-ДД.");

    public static IReadOnlyList<ScheduleEntry> Parse(string text)
    {
        if (text.Length > 12000)
            throw new ArgumentException("Слишком длинное расписание. Отправьте не более 20 занятий.");
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length is < 1 or > 20)
            throw new ArgumentException("Отправьте от 1 до 20 занятий.");
        var result = new List<ScheduleEntry>();
        foreach (var line in lines)
        {
            var fields = line.Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length != 5 || !TimeOnly.TryParseExact(fields[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                !TimeOnly.TryParseExact(fields[2], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                throw new ArgumentException("Формат строки: дата | начало | конец | предмет | место. Время — ЧЧ:ММ.");
            result.Add(new(Guid.NewGuid(), Date(fields[0]), start, end, fields[3], fields[4]));
        }
        Validate(result);
        return result;
    }

    public static void Validate(IReadOnlyList<ScheduleEntry> entries)
    {
        if (entries.Count is < 1 or > 20)
            throw new ArgumentException("Допустимо от 1 до 20 занятий.");
        foreach (var entry in entries)
            if (entry.Id == Guid.Empty || entry.End <= entry.Start || string.IsNullOrWhiteSpace(entry.Title) ||
                entry.Title.Length > 200 || entry.Location.Length > 200 || entry.Title.Contains('\0') || entry.Location.Contains('\0'))
                throw new ArgumentException("Проверьте занятие: название до 200 символов, конец позже начала.");
        if (entries.Select(x => (x.Date, x.Start, x.End, x.Title, x.Location)).Distinct().Count() != entries.Count)
            throw new ArgumentException("В расписании повторяется одно и то же занятие.");
    }
}
