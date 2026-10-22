using IntellectBot.Domain;
using IntellectBot.Telegram;
using Xunit;

namespace IntellectBot.Tests;

public sealed class PresentationTests
{
    private static string Text(string value) => string.Concat(MessageFormatter.Format(new(value)).Select(part => part.Text));

    [Fact]
    public void SolidTableIsReadableWithoutPipeMarkup()
    {
        var text = Text("Как соотносятся принципы\n| GRASP | Близкий SOLID-принцип |\n|---|---|\n| High Cohesion | SRP |\n| Low Coupling | DIP |\n\nКонец.");
        Assert.Contains("• High Cohesion\n  Близкий SOLID-принцип: SRP", text);
        Assert.Contains("• Low Coupling\n  Близкий SOLID-принцип: DIP", text);
        Assert.Contains("Конец.", text);
        Assert.DoesNotContain('|', text);
    }

    [Fact]
    public void TablePreservesAllColumnsAndInlineCode()
    {
        var text = Text("| Имя | Код | Значение |\n|:---|---:|:---:|\n| Маска | `a | b` | **Верно** |\n");
        Assert.Contains("• Маска", text);
        Assert.Contains("Код: <code>a | b</code>", text);
        Assert.Contains("Значение: Верно", text);
        Assert.DoesNotContain("**", text);
    }

    [Fact]
    public void ATableInsideCodeIsNotChanged()
    {
        const string table = "| Имя | Значение |\n|---|---|\n| a | b |\n";
        var text = Text("Пример\n```text\n" + table + "```\n");
        Assert.Contains("<pre>" + table + "</pre>", text);
    }

    [Fact]
    public void SinglePipeInProseIsNotATable()
    {
        Assert.Equal("Оператор a | b работает с битами.", Text("Оператор a | b работает с битами."));
    }

    [Fact]
    public void WeatherArrowDoesNotTurnWholeReplyIntoTranslationCode()
    {
        var parts = MessageFormatter.Format(new("Томск, Россия → погода\n\nТемпература: **0 °C**\nВетер: 2 м/с")).ToArray();
        Assert.All(parts, part => Assert.Null(part.ParseMode));
        Assert.DoesNotContain("**", string.Concat(parts.Select(part => part.Text)));
    }

    [Fact]
    public void TranslationStillHasCopyableTextAndRussianLanguageNames()
    {
        var text = Text("English → Russian\n\nПривет, мир!");
        Assert.Equal("Английский → Русский\n\n<code>Привет, мир!</code>", text);
    }

    [Theory]
    [InlineData("Europe/Moscow", "21.10.2026 09:00")]
    [InlineData("Asia/Tokyo", "21.10.2026 15:00")]
    [InlineData("America/New_York", "21.10.2026 02:00")]
    public void ReminderAuditConvertsUtcBeforeFormatting(string zone, string expected)
    {
        var draft = new ReminderDraft("Поставить чайник", DateTimeOffset.Parse("2026-10-21T06:00:00Z"), zone);
        Assert.Contains("время: " + expected, ReminderDisplay.Summary(draft));
        Assert.DoesNotContain(":00:00", ReminderDisplay.Summary(draft));
        Assert.Equal(expected, ReminderDisplay.InZone(draft.RemindAt, zone));
    }
    [Fact]
    public void EmptyLastCellStillProducesReadableCard()
    {
        var text = Text("| Имя | Тип | Описание |\n|---|---|---|\n| a | int ||\n");
        Assert.Contains("• a", text);
        Assert.Contains("Тип: int", text);
        Assert.Contains("Описание:", text);
        Assert.DoesNotContain('|', text);
    }

    [Fact]
    public void EscapedPipeBelongsToCellInsteadOfCreatingAnotherColumn()
    {
        var text = Text("| Имя | Пример |\n|---|---|\n| Маска | a \\| b |\n");
        Assert.Contains("Пример: a | b", text);
        Assert.DoesNotContain("|---|", text);
    }

}
