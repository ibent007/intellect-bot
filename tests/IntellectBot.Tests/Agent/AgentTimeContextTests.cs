using IntellectBot.Agent;
using Xunit;

namespace IntellectBot.Tests;

public sealed class AgentTimeContextTests
{
    [Fact]
    public void LocalDateAndWeekdayFollowUserZoneAcrossMidnight()
    {
        var utc = new DateTimeOffset(2026, 10, 19, 16, 20, 0, TimeSpan.Zero);
        var rules = AgentTimeContext.Rules(utc, "Asia/Tokyo");

        Assert.Contains("2026-10-20T01:20:00", rules);
        Assert.Contains("вторник", rules);
        Assert.Contains("Asia/Tokyo", rules);
    }
}
