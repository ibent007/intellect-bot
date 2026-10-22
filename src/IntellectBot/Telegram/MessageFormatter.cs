using System.Text;
using System.Text.RegularExpressions;
using IntellectBot.Domain;

namespace IntellectBot.Telegram;

public static class MessageFormatter
{
    // Telegram считает длину в UTF-16: не разрываем пару суррогатов emoji.
    public static IEnumerable<string> Split(string text, int limit = 4000)
    {
        if (limit < 2)
            throw new ArgumentOutOfRangeException(nameof(limit));
        for (var start = 0; start < text.Length;)
        {
            var count = Math.Min(limit, text.Length - start);
            if (start + count < text.Length && char.IsHighSurrogate(text[start + count - 1]) && char.IsLowSurrogate(text[start + count]))
                count--;
            yield return text.Substring(start, count);
            start += count;
        }
    }
    public static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#x27;");
    public static IEnumerable<OutgoingText> Format(LlmResponse response)
    {
        var normalized = response.Text.Replace("\r\n", "\n");
        var separator = normalized.IndexOf("\n\n", StringComparison.Ordinal);

        if (separator > 0)
        {
            var header = normalized[..separator];
            var body = normalized[(separator + 2)..];

            if (!string.IsNullOrWhiteSpace(body) && IsTranslationHeader(header))
            {
                var first = true;
                foreach (var part in Split(TranslationReply.Body(body), 3900))
                {
                    yield return new((first ? Escape(RussianHeader(header)) + "\n\n" : "") + "<code>" + Escape(part) + "</code>", "HTML");
                    first = false;
                }
                yield break;
            }
        }
        foreach (var part in FormatCode(response.Text))
            yield return part;
    }
    private static IEnumerable<OutgoingText> FormatCode(string text)
    {
        var matches = Regex.Matches(text,
            @"```[^\r\n]*\r?\n(?<block>[\s\S]*?)(?:```|\z)|`(?<inline>[^`\r\n]+)`");

        if (matches.Count == 0)
        {
            foreach (var part in Split(PlainTextStyle.Clean(text)))
                yield return new(part);

            yield break;
        }

        // Временно заменяем код маркерами, чтобы снять разметку даже вокруг `кода`.
        // Содержимое кода не передаётся очистителю и восстанавливается без изменений.
        var protectedCode = new List<(string Text, string Tag)>();
        var tokenPrefix = "\uE000" + Guid.NewGuid().ToString("N") + ":";
        var prose = new StringBuilder();
        var offset = 0;
        foreach (Match match in matches)
        {
            prose.Append(text[offset..match.Index]);
            prose.Append(tokenPrefix).Append(protectedCode.Count).Append('\uE001');
            var block = match.Groups["block"];
            protectedCode.Add((block.Success ? block.Value : match.Groups["inline"].Value,
                block.Success ? "pre" : "code"));
            offset = match.Index + match.Length;
        }
        prose.Append(text[offset..]);
        var cleaned = PlainTextStyle.Clean(prose.ToString());
        var segments = new List<(string Text, string Tag)>();
        offset = 0;
        foreach (Match marker in Regex.Matches(cleaned, Regex.Escape(tokenPrefix) + @"(?<index>\d+)\uE001"))
        {
            segments.Add((cleaned[offset..marker.Index], ""));
            segments.Add(protectedCode[int.Parse(marker.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture)]);
            offset = marker.Index + marker.Length;
        }
        segments.Add((cleaned[offset..], ""));

        var buffer = new StringBuilder();
        var length = 0;

        foreach (var segment in segments)
        {
            foreach (var part in Split(segment.Text, 3500))
            {
                if (length + part.Length > 3500)
                {
                    yield return new(buffer.ToString(), "HTML");
                    buffer.Clear();
                    length = 0;
                }

                buffer.Append(segment.Tag.Length == 0
                    ? Escape(part)
                    : $"<{segment.Tag}>{Escape(part)}</{segment.Tag}>");

                length += part.Length;
            }
        }

        if (buffer.Length > 0)
            yield return new(buffer.ToString(), "HTML");
    }
    private static bool IsTranslationHeader(string header)
    {
        var names = header.Split('→');
        return names.Length == 2 && names.All(name => LanguageNames.Values.Contains(
            RussianLanguage(name), StringComparer.OrdinalIgnoreCase));
    }

    private static string RussianLanguage(string name)
    {
        name = name.Trim().Trim('*', '`').Trim();
        return LanguageNames.GetValueOrDefault(name, name);
    }

    private static readonly IReadOnlyDictionary<string, string> LanguageNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Russian"] = "Русский",
        ["English"] = "Английский",
        ["German"] = "Немецкий",
        ["French"] = "Французский",
        ["Spanish"] = "Испанский",
        ["Italian"] = "Итальянский",
        ["Portuguese"] = "Португальский",
        ["Chinese"] = "Китайский",
        ["Japanese"] = "Японский",
        ["Korean"] = "Корейский",
        ["Arabic"] = "Арабский",
        ["Turkish"] = "Турецкий",
        ["Polish"] = "Польский",
        ["Ukrainian"] = "Украинский",
        ["Finnish"] = "Финский",
        ["Estonian"] = "Эстонский",
        ["Belarusian"] = "Белорусский",
        ["Dutch"] = "Нидерландский",
        ["Swedish"] = "Шведский",
        ["Norwegian"] = "Норвежский",
        ["Greek"] = "Греческий",
        ["Hebrew"] = "Иврит",
        ["Hindi"] = "Хинди",
        ["Czech"] = "Чешский"
    };

    private static string RussianHeader(string header)
    {
        return string.Join(" → ", header.Split('→').Select(RussianLanguage));
    }
}
