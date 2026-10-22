using IntellectBot.Domain;
using Xunit;

namespace IntellectBot.Tests;

public sealed class AgentStateValidationTests
{
    [Theory]
    [InlineData("Europe/Moscow")]
    [InlineData("Europe/Berlin")]
    [InlineData("America/New_York")]
    public void ExistingIanaZoneIsAccepted(string zone)
    {
        Assert.Equal(
            zone,
            AgentStateValidation.ValidateTimeZone($" {zone} "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown/Zone")]
    [InlineData("Russian Standard Time")]
    public void InvalidOrWindowsZoneIsRejected(string zone)
    {
        Assert.Throws<ArgumentException>(() =>
            AgentStateValidation.ValidateTimeZone(zone));
    }

    [Fact]
    public void AuditRejectsThirdTool()
    {
        var tool = new ToolAuditEntry(
            "get_weather", "Город: Берлин", "passed", "success");

        var audit = new AgentAuditEntry(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-10-11T12:00:00Z"),
            "call_tool",
            [tool, tool, tool],
            "Нужны погодные данные.");

        Assert.Throws<ArgumentException>(() =>
            AgentStateValidation.ValidateAudit(audit));
    }

    [Fact]
    public void AuditDoesNotExposeContentsThroughToString()
    {
        var tool = new ToolAuditEntry(
            "get_weather", "private summary", "passed", "success");

        var audit = new AgentAuditEntry(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-10-11T12:00:00Z"),
            "call_tool",
            [tool],
            "private reason");

        Assert.DoesNotContain("private", tool.ToString());
        Assert.DoesNotContain("private", audit.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidOwnerIsRejected(long owner)
    {
        Assert.Throws<ArgumentException>(() =>
            AgentStateValidation.ValidateOwner(owner));
    }
    [Fact]
    public void DuplicateReminderOutcomeIsAValidAuditStatus()
    {
        AgentStateValidation.ValidateAudit(new AgentAuditEntry(Guid.NewGuid(), DateTimeOffset.UtcNow,
            "call_tool", [new ToolAuditEntry("add_reminder", "Совпадающие текст и время уже сохранены.", "passed", "already_exists")],
            "Дубликат не создан."));
    }

}
