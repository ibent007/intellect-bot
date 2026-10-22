using IntellectBot.Domain;
using Xunit;

namespace IntellectBot.Tests;

public sealed class ReminderValidationTests
{
    [Fact]
    public void ExplicitOffsetIsNormalizedToUtc()
    {
        var draft = ReminderValidation.Parse(
            "  Проверить код  ",
            "2026-10-12T18:00:00+03:00",
            "Europe/Moscow");

        Assert.Equal("Проверить код", draft.Text);
        Assert.Equal(
            DateTimeOffset.Parse("2026-10-12T15:00:00Z"),
            draft.RemindAt);
        Assert.Equal(TimeSpan.Zero, draft.RemindAt.Offset);
        Assert.Equal("Europe/Moscow", draft.TimeZone);
    }

    [Theory]
    [InlineData("2026-10-12T18:00:00")]
    [InlineData("12.10.2026 18:00")]
    [InlineData("завтра")]
    [InlineData("2026-02-30T18:00:00Z")]
    public void MissingOffsetOrInvalidTimeIsRejected(string value)
    {
        var error = Assert.Throws<ReminderException>(() =>
            ReminderValidation.Parse("Задание", value, "Europe/Moscow"));

        Assert.Equal("invalid_time", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTextIsRejected(string text)
    {
        var error = Assert.Throws<ReminderException>(() =>
            ReminderValidation.Parse(
                text, "2026-10-12T18:00:00Z", "Europe/Moscow"));

        Assert.Equal("invalid_text", error.Code);
    }

    [Fact]
    public void LongTextIsRejected()
    {
        Assert.Throws<ReminderException>(() =>
            ReminderValidation.Parse(
                new string('a', 501),
                "2026-10-12T18:00:00Z",
                "Europe/Moscow"));
    }

    [Fact]
    public void PastTimeIsRejected()
    {
        var draft = ReminderValidation.Parse(
            "Задание", "2026-10-12T12:00:00Z", "Europe/Moscow");

        var error = Assert.Throws<ReminderException>(() =>
            ReminderValidation.RequireFuture(
                draft, DateTimeOffset.Parse("2026-10-12T12:00:00Z")));

        Assert.Equal("past_time", error.Code);
    }
}
