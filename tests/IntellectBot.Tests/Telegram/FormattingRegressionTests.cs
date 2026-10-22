using IntellectBot.Domain;
using IntellectBot.Telegram;
using Xunit;

namespace IntellectBot.Tests;

public sealed class FormattingRegressionTests
{
    [Fact]
    public void EscapedMarkdownNumbersAndNarrowSpacesDoNotLeakIntoProse()
    {
        var response = new LlmResponse("/>## Погода\n\n1\\. **Москва**\nТемпература\u202f—\u202f1\u00a0°C");
        var text = string.Concat(MessageFormatter.Format(response).Select(part => part.Text));

        Assert.Contains("1. Москва", text);
        Assert.DoesNotContain("\\.", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("/>", text);
        Assert.DoesNotContain("##", text);
        Assert.Contains("Температура — 1 °C", text);
    }

    [Fact]
    public void CleaningProsePreservesCodeUnderscoresOperatorsAndIndentation()
    {
        const string code = "class OrderNotifier\n{\n    private string _email = \"<tag>&\";\n}";
        var response = new LlmResponse("**Пример**\n\n```csharp\n" + code + "\n```\n\nГотово.");
        var parts = MessageFormatter.Format(response).ToArray();
        var text = string.Concat(parts.Select(part => part.Text));

        Assert.All(parts, part => Assert.Equal("HTML", part.ParseMode));
        Assert.Contains("<pre>", text);
        Assert.Contains("    private string _email", text);
        Assert.Contains("&lt;tag&gt;&amp;", text);
        Assert.DoesNotContain("**", text);
    }
}
