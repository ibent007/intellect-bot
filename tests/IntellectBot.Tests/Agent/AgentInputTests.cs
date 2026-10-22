using IntellectBot.Agent;
using IntellectBot.Domain;
using Xunit;

namespace IntellectBot.Tests;

public sealed class AgentInputTests
{
    [Theory]
    [InlineData("{\"date\":\"2026-10-12\",\"owner\":42}")]
    [InlineData("{\"date\":\"2026-10-12\",\"date\":\"2026-10-13\"}")]
    [InlineData("{\"date\":42}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("broken")]
    public void BadArgumentsAreRejected(string json) => Assert.Throws<ReminderException>(() =>
        AgentInput.Validate(AgentTestSupport.Tool("get_schedule", "date"), json));

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("12.10.2026")]
    [InlineData("tomorrow")]
    public void InvalidDatesAreRejected(string date) => Assert.Throws<ReminderException>(() =>
        AgentInput.Validate(AgentTestSupport.Tool("get_schedule", "date"), System.Text.Json.JsonSerializer.Serialize(new
        {
            date
        })));

    [Fact]
    public void WhitespaceCityIsRejected() => Assert.Throws<ReminderException>(() =>
        AgentInput.Validate(AgentTestSupport.Tool("get_weather", "city"), System.Text.Json.JsonSerializer.Serialize(new
        {
            city = "   "
        })));
}
