using System.Text.Json;

namespace IntellectBot.Agent;

// Модель группирует номера проверенных фрагментов. Текст фактов и ссылки не генерируются заново.
internal static class EvidencePresentation
{
    private static readonly HashSet<string> Headings = ["Кратко", "Биография", "Чем известен", "Чем известна", "Работы", "Основные факты", "Подробности"];
    public static JsonElement Source(IEnumerable<JsonElement> messages) => ToolResultContext.Data(messages)
        .FirstOrDefault(data => data.TryGetProperty("facts", out var facts) && facts.ValueKind == JsonValueKind.Array && facts.GetArrayLength() > 0);

    public static string? Render(IEnumerable<JsonElement> messages, string answer)
    {
        var source = Source(messages);
        if (source.ValueKind != JsonValueKind.Object)
            return null;
        var fallback = source.GetProperty("public_text").GetString();
        try
        {
            using var document = JsonDocument.Parse(answer.Trim().Trim('`').Trim().StartsWith("json", StringComparison.Ordinal)
                ? answer.Trim().Trim('`').Trim()[4..].Trim() : answer.Trim().Trim('`').Trim());
            var facts = source.GetProperty("facts").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var sections = document.RootElement.GetProperty("sections");
            if (sections.ValueKind != JsonValueKind.Array || sections.GetArrayLength() is < 1 or > 6)
                return fallback;
            var used = new HashSet<int>();
            var blocks = new List<string> { source.GetProperty("title").GetString()! };
            foreach (var section in sections.EnumerateArray())
            {
                var heading = section.GetProperty("heading").GetString()!;
                if (!Headings.Contains(heading))
                    return fallback;
                var lines = new List<string>();
                foreach (var id in section.GetProperty("facts").EnumerateArray())
                {
                    if (!id.TryGetInt32(out var index) || index < 0 || index >= facts.Length || !used.Add(index))
                        return fallback;
                    lines.Add("• " + facts[index]);
                }
                if (lines.Count == 0)
                    return fallback;
                blocks.Add(heading + "\n" + string.Join("\n\n", lines));
            }
            blocks.Add("Источник: " + source.GetProperty("url").GetString() + "\nВикипедия · CC BY-SA.");
            return string.Join("\n\n", blocks);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return fallback;
        }
    }
}
