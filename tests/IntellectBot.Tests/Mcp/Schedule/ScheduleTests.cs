using System.Globalization;
using System.Text.Json;
using IntellectBot.McpServer.Schedule;
using Xunit;

namespace IntellectBot.Tests;

public sealed class ScheduleTests
{
    [Fact]
    public void ExistingDateReturnsExactLesson()
    {
        var service = Create("2026-10-05", "2026-10-10T00:00:00Z");

        var result = service.GetDay("2026-10-05");

        Assert.Equal("2026-10-05", result.Date);
        Assert.Equal("Europe/Moscow", result.TimeZone);

        var lesson = Assert.Single(result.Lessons);

        Assert.Equal("09:00", lesson.Start);
        Assert.Equal("10:30", lesson.End);
        Assert.Equal("Программирование", lesson.Title);
        Assert.Equal("Аудитория 101", lesson.Location);
    }

    [Fact]
    public void CoveredDateWithoutLessonsReturnsEmptyArray()
    {
        var service = Create("2026-10-05", "2026-10-10T00:00:00Z");

        var result = service.GetDay("2026-10-06");

        Assert.Equal("2026-10-06", result.Date);
        Assert.Empty(result.Lessons);
    }

    [Theory]
    [InlineData("")]
    [InlineData("завтра")]
    [InlineData("05.10.2026")]
    [InlineData("2026-2-01")]
    [InlineData("2026-02-30")]
    [InlineData(" 2026-10-05")]
    public void InvalidDateIsRejected(string date)
    {
        var service = Create("2026-10-05", "2026-10-10T00:00:00Z");

        Assert.Throws<ArgumentException>(() => service.GetDay(date));
    }

    [Fact]
    public void DateOutsideCoverageIsNotReportedAsFreeDay()
    {
        var service = Create("2026-10-05", "2026-10-10T00:00:00Z");

        Assert.Throws<ScheduleUnavailableException>(
            () => service.GetDay("2026-10-19"));
    }

    [Theory]
    [InlineData(
        "2026-09-28", "2026-10-01T12:00:00Z",
        "2026-09-28", "2026-10-04")]
    [InlineData(
        "2026-12-28", "2027-01-01T12:00:00Z",
        "2026-12-28", "2027-01-03")]
    [InlineData(
        "2026-10-05", "2026-10-11T21:30:00Z",
        "2026-10-12", "2026-10-18")]
    public void WeekUsesConfiguredZoneAndHandlesBoundaries(
        string from,
        string now,
        string expectedStart,
        string expectedEnd)
    {
        var service = Create(from, now);

        var week = service.GetCurrentWeek();

        Assert.Equal(expectedStart, week.WeekStart);
        Assert.Equal(expectedEnd, week.WeekEnd);
        Assert.Equal("Europe/Moscow", week.TimeZone);
        Assert.Equal(7, week.Days.Length);

        var monday = DateOnly.ParseExact(
            expectedStart, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        for (var index = 0; index < 7; index++)
        {
            Assert.Equal(
                monday.AddDays(index).ToString(
                    "yyyy-MM-dd", CultureInfo.InvariantCulture),
                week.Days[index].Date);

            Assert.Equal("Europe/Moscow", week.Days[index].TimeZone);
        }
    }

    [Fact]
    public void InvalidZoneIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new DemoScheduleSource("{}", "Unknown/Zone", TimeProvider.System));
    }

    [Fact]
    public void ShortCoverageIsRejected()
    {
        var json = """
            {
              "from": "2026-10-05",
              "through": "2026-10-06",
              "days": []
            }
            """;

        Assert.Throws<InvalidDataException>(() =>
            new DemoScheduleSource(json, "Europe/Moscow", TimeProvider.System));
    }

    [Fact]
    public void WeekOutsideCoverageIsRejected()
    {
        var service = Create("2026-10-05", "2026-10-20T00:00:00Z");

        Assert.Throws<ScheduleUnavailableException>(
            () => service.GetCurrentWeek());
    }

    private static DemoScheduleSource Create(string from, string now)
    {
        var start = DateOnly.ParseExact(
            from, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        var document = new ScheduleDocument(
            from,
            start.AddDays(13).ToString(
                "yyyy-MM-dd", CultureInfo.InvariantCulture),
            [
                new ScheduleDay(
                    from,
                    [
                        new Lesson(
                            "09:00", "10:30",
                            "Программирование", "Аудитория 101")
                    ])
            ]);

        return new DemoScheduleSource(
            JsonSerializer.Serialize(document),
            "Europe/Moscow",
            new FixedTimeProvider(
                DateTimeOffset.Parse(
                    now, CultureInfo.InvariantCulture)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
