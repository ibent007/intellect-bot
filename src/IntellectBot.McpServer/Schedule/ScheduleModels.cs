using System.Text.Json.Serialization;

namespace IntellectBot.McpServer.Schedule;

public sealed record Lesson(
    [property: JsonPropertyName("start")] string Start,
    [property: JsonPropertyName("end")] string End,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("location")] string Location);

public sealed record ScheduleDay(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("lessons")] Lesson[] Lessons);

public sealed record ScheduleDocument(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("through")] string Through,
    [property: JsonPropertyName("days")] ScheduleDay[] Days);

public sealed record DaySchedule(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("timezone")] string TimeZone,
    [property: JsonPropertyName("lessons")] Lesson[] Lessons);

public sealed record WeekSchedule(
    [property: JsonPropertyName("week_start")] string WeekStart,
    [property: JsonPropertyName("week_end")] string WeekEnd,
    [property: JsonPropertyName("timezone")] string TimeZone,
    [property: JsonPropertyName("days")] DaySchedule[] Days);

public sealed class ScheduleUnavailableException(string message)
    : Exception(message);
