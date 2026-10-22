using System.Text.RegularExpressions;

namespace IntellectBot.Domain;

// Контракт перевода отделён от разметки обычных ответов и программного кода.
public static class TranslationReply
{
    public static bool IsRequest(string question) => Direction(question).Success || Regex.IsMatch(question,
        @"\b(?:переведи(?:те)?|перевод(?:и|ите)?)\b", RegexOptions.IgnoreCase);

    public static bool IsValid(string answer) => Regex.IsMatch(answer,
        @"\A\s*[А-ЯЁа-яё-]{3,30}\s*→\s*[А-ЯЁа-яё-]{3,30}\s*\r?\n\s*\r?\n\S", RegexOptions.CultureInvariant) &&
        !answer.StartsWith("Язык", StringComparison.OrdinalIgnoreCase);

    public static string Body(string body)
    {
        var trimmed = body.Trim();
        var fenced = Regex.Match(trimmed, @"\A```[^\r\n]*\r?\n(?<body>[\s\S]*?)\r?\n?```\z");
        return fenced.Success ? fenced.Groups["body"].Value.TrimEnd() : trimmed;
    }

    private static Match Direction(string question) => Regex.Match(question,
        @"\b(?:перев(?:еди(?:те)?|од(?:и|ите)?)|теперь)\s+на\s+(?<target>[а-яё-]+)(?:\s+язык)?\s*:?\s*", RegexOptions.IgnoreCase);

    public static string Instructions(string question) =>
        "Сейчас выполняется перевод. Команда может стоять до или после исходного текста. " +
        "Определи язык самого исходного текста, а не русской команды. " +
        "Не используй направление предыдущего перевода. " +
        "Первая строка: название исходного языка по-русски → название целевого языка по-русски. " +
        "Затем пустая строка и только переведённый текст. " +
        "Не копируй исходник вместо перевода. " +
        (Direction(question) is { Success: true } match
            ? "Целевой язык: " + match.Groups["target"].Value + "."
            : "");

    public static bool MatchesRequest(string answer, string question)
    {
        if (!IsValid(answer))
            return false;

        var normalized = answer.Replace("\r\n", "\n");
        var split = normalized.IndexOf("\n\n", StringComparison.Ordinal);

        if (split < 0)
            return false;

        var names = normalized[..split].Trim().Split('→');
        var body = Body(normalized[(split + 2)..]);
        var match = Direction(question);

        if (!match.Success)
            return true;

        var target = match.Groups["target"].Value;

        if (!names[1].Trim().Equals(target, StringComparison.OrdinalIgnoreCase))
            return false;

        var source = question.Remove(match.Index, match.Length)
            .Trim(' ', '\r', '\n', ':', '.', '!', '?');

        if (names[0].Trim().Equals("Русский", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(source, @"\p{L}") &&
            !Regex.IsMatch(source, @"[А-Яа-яЁё]"))
            return false;

        if (!names[0].Trim().Equals(target, StringComparison.OrdinalIgnoreCase) &&
            body.Trim(' ', '.', '!', '?').Equals(source, StringComparison.OrdinalIgnoreCase))
            return false;

        var script = target.ToLowerInvariant() switch
        {
            "русский" => @"[А-Яа-яЁё]",
            "китайский" => @"\p{IsCJKUnifiedIdeographs}",
            "японский" => @"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]",
            "английский" or "немецкий" or "испанский" or "французский" => @"[A-Za-z]",
            _ => null
        };

        return !Regex.IsMatch(source, @"\p{L}") ||
            script is null ||
            Regex.IsMatch(body, script);
    }
}
