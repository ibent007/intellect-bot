using System.Text.RegularExpressions;
using IntellectBot.Domain;

namespace IntellectBot.Telegram;

// Снимает разметку только с обычного текста; код обрабатывается отдельно.
public static class PlainTextStyle
{
    public static string Clean(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\u00a0', ' ').Replace('\u202f', ' ');
        text = ReadableMath(text);
        text = Regex.Replace(text, @"\\([\\`*_{}\[\]()#+\-.!>])", "$1");
        text = Regex.Replace(text, @"(?m)^[ \t]*>[ \t]?", "");
        text = Regex.Replace(text, @"\A\s*/>\s*", "");
        text = Regex.Replace(text, @"(?m)^[ \t]{0,3}#{1,6}[ \t]+(.*?)(?:[ \t]+#+[ \t]*)?\r?$", "$1");
        text = Regex.Replace(text, @"(?m)^([ \t]*)\*[ \t]+", "$1• ");
        text = Regex.Replace(text, @"(?m)^[ \t]*(?:\*[ \t]*){3,}\r?$", "");
        foreach (var marker in new[] { "\\*\\*", "__", "~~" })
            text = Regex.Replace(text, $@"{marker}(?=\S)(.+?)(?<=\S){marker}", "$1");
        text = Regex.Replace(text, @"(?<![\w\\])\*(?=\S)(.+?)(?<=\S)\*(?!\w)", "$1");
        text = Regex.Replace(text, @"(?<![\w\\])_(?=\S)(.+?)(?<=\S)_(?!\w)", "$1");
        return DisplayTime.Simplify(MarkdownTableFormatter.Convert(text));
    }

    private static string ReadableMath(string text) => Regex.Replace(text,
        @"\\\[(?<formula>[\s\S]*?)\\\]|\\\((?<formula>[\s\S]*?)\\\)|\$\$(?<formula>[\s\S]*?)\$\$",
        match =>
        {
            var formula = match.Groups["formula"].Value.Trim();

            formula = Regex.Replace(
                formula, @"\\frac\{([^{}]*)\}\{([^{}]*)\}", "($1)/($2)");

            formula = Regex.Replace(
                formula, @"\\sqrt\{([^{}]*)\}", "√($1)");

            formula = Regex.Replace(formula, @"\^(?:\{2\}|2(?!\d))", "²");
            formula = Regex.Replace(formula, @"\^(?:\{3\}|3(?!\d))", "³");

            formula = Regex.Replace(
                formula, @"\\(?:boxed|mathrm|text)\{([^{}]*)\}", "$1");

            var symbols = new Dictionary<string, string>
            {
                ["neq"] = "≠",
                ["ne"] = "≠",
                ["leq"] = "≤",
                ["geq"] = "≥",
                ["times"] = "×",
                ["cdot"] = "·",
                ["pm"] = "±",
                ["pi"] = "π",
                ["quad"] = " ",
                ["qquad"] = " ",
                ["left"] = "",
                ["right"] = ""
            };

            return Regex.Replace(
                formula,
                @"\\([a-zA-Z]+)",
                token => symbols.GetValueOrDefault(
                    token.Groups[1].Value, token.Value));
        });
}
