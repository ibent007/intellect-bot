using IntellectBot.Domain;

namespace IntellectBot.Telegram;

public static class MessageFormatter
{
    // Telegram entity offsets/lengths use UTF-16. Never cut a surrogate pair.
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
        var separator = response.Text.IndexOf("\n\n", StringComparison.Ordinal);
        if (response.Mode == "translate" && separator > 0)
        {
            var header = response.Text[..separator];
            var body = response.Text[(separator + 2)..];
            if (!string.IsNullOrWhiteSpace(body) && header.Count(c => c == '→') == 1 && !header.Contains('\n') && ContextBuilder.Length(header) <= 80)
            {
                var first = true;
                foreach (var part in Split(body, 3900))
                {
                    yield return new((first ? Escape(header) + "\n\n" : "") + "<code>" + Escape(part) + "</code>", "HTML");
                    first = false;
                }
                yield break;
            }
        }
        foreach (var part in Split(response.Text))
            yield return new(part);
    }
}
