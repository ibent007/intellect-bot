using IntellectBot.Data;
using IntellectBot.Domain;
namespace IntellectBot.McpServer.Schedule;

// Преобразует личные записи PostgreSQL в публичный контракт MCP.
public sealed class PersonalScheduleReader(PersonalScheduleStore store, AgentStateStore users, TimeProvider clock)
{
    public async Task<DaySchedule> GetDayAsync(long owner, string date, CancellationToken ct)
    {
        var parsed = ScheduleInput.Date(date);
        var zone = await Zone(owner, ct);
        var entries = await store.ReadAsync(owner, parsed, parsed, ct);
        return Day(parsed, zone, entries);
    }
    public async Task<WeekSchedule> GetWeekAsync(long owner, CancellationToken ct)
    {
        var zone = await Zone(owner, ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var entries = await store.ReadAsync(owner, monday, monday.AddDays(6), ct);
        return new(monday.ToString("yyyy-MM-dd"), monday.AddDays(6).ToString("yyyy-MM-dd"), zone,
            Enumerable.Range(0, 7).Select(i => Day(monday.AddDays(i), zone, entries)).ToArray());
    }
    private async Task<string> Zone(long owner, CancellationToken ct) => await users.GetTimeZoneAsync(owner, ct) ??
        throw new ScheduleUnavailableException("Сначала выберите часовой пояс в настройках.");
    private static DaySchedule Day(DateOnly date, string zone, IReadOnlyList<ScheduleEntry> entries) =>
        new(date.ToString("yyyy-MM-dd"), zone, entries.Where(x => x.Date == date).Select(x =>
            new Lesson(x.Start.ToString("HH:mm"), x.End.ToString("HH:mm"), x.Title, x.Location)).ToArray());
}
