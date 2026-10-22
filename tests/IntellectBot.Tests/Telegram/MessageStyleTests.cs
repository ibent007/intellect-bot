using System.Net;
using System.Text.RegularExpressions;
using IntellectBot.Telegram;
using Xunit;

namespace IntellectBot.Tests;

public sealed class MessageStyleTests
{
    [Fact]
    public void MarkdownWrappingInlineCodeIsRemovedWithoutChangingCode()
    {
        var result = Assert.Single(MessageFormatter.Format(new("## **`x ** 2`**\n**Переменная `name`:** значение")));
        Assert.Equal("<code>x ** 2</code>\nПеременная <code>name</code>: значение", result.Text);
    }

    [Fact]
    public void PlainMarkdownMarkersAreRemovedButMathAndNamesRemain()
    {
        var result = Assert.Single(MessageFormatter.Format(new("## Погода\n**Ясно**, *тепло*.\n* Температура\nC# и student_score: 2 * 3 = 6")));
        Assert.Equal("Погода\nЯсно, тепло.\n• Температура\nC# и student_score: 2 * 3 = 6", result.Text);
        Assert.Null(result.ParseMode);
    }

    [Fact]
    public void CodeIsUnchangedWhileExplanationsAreCleaned()
    {
        const string code = "# comment\nx = 2 ** 3\nprint(\"**value** 19:30:34+03:00\")\n";
        var result = Assert.Single(MessageFormatter.Format(new("### Пример\n**Код:**\n```python\n" + code + "```\n**Готово**")));
        Assert.Equal("HTML", result.ParseMode);
        Assert.Contains("<pre>" + MessageFormatter.Escape(code) + "</pre>", result.Text);
        Assert.StartsWith("Пример\nКод:", result.Text);
        Assert.EndsWith("Готово", result.Text);
    }

    [Fact]
    public void InlineCodeAndTranslationPreserveLiteralSymbols()
    {
        var result = Assert.Single(MessageFormatter.Format(new("**Выражение** `a ** b`")));
        Assert.Equal("Выражение <code>a ** b</code>", result.Text);
        result = Assert.Single(MessageFormatter.Format(new("Russian → English\n\n**literal** #tag")));
        Assert.Equal("Русский → Английский\n\n<code>**literal** #tag</code>", result.Text);
    }

    [Fact]
    public void TimeDisplayDropsSecondsAndOffsetOutsideCode()
    {
        var result = Assert.Single(MessageFormatter.Format(new("Данные: 2030-01-15T18:30:45+03:00; обновлено 18:31:00 +03:00.")));
        Assert.Equal("Данные: 15.01.2030 18:30; обновлено 18:31.", result.Text);
    }

    [Fact]
    public void LongCodeIsSplitWithoutLosingCharactersOrBreakingHtml()
    {
        var code = string.Concat(Enumerable.Repeat("🙂 <x> ** #\n", 1000));
        var parts = MessageFormatter.Format(new("```text\n" + code + "```")).ToArray();
        Assert.True(parts.Length > 1);
        Assert.All(parts, part =>
        {
            Assert.Equal("HTML", part.ParseMode);
            Assert.StartsWith("<pre>", part.Text);
            Assert.EndsWith("</pre>", part.Text);
        });
        var actual = string.Concat(parts.Select(part => WebUtility.HtmlDecode(Regex.Replace(part.Text, "</?pre>", ""))));
        Assert.Equal(code, actual);
    }
}
