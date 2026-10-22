using System.Text.Json;

namespace IntellectBot.Agent;

// Публичное представление может прийти только из проверенного результата MCP, а не из текста модели.
internal static class ToolResultContext
{
    internal static IEnumerable<JsonElement> Data(IEnumerable<JsonElement> messages)
    {
        foreach (var message in messages.Reverse().TakeWhile(item => item.GetProperty("role").GetString() != "user"))
        {
            if (message.GetProperty("role").GetString() != "tool" ||
                !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                continue;
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(content.GetString()!);
            }
            catch (JsonException) { }
            if (document is null)
                continue;
            using (document)
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    yield return data.Clone();
        }
    }
    public static IEnumerable<string> PublicTexts(IEnumerable<JsonElement> messages) => Data(messages).Reverse()
        .Where(data => data.TryGetProperty("public_text", out var text) && text.ValueKind == JsonValueKind.String &&
            text.GetString() is { Length: > 0 and <= 12000 })
        .Select(data => data.GetProperty("public_text").GetString()!);
    public static string? EvidenceKind(IEnumerable<JsonElement> messages) => Data(messages)
        .Select(data => data.TryGetProperty("evidence_kind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null)
        .FirstOrDefault(kind => kind is not null);
    public static bool HasEvidence(IEnumerable<JsonElement> messages, string kind) => Data(messages)
        .Any(data => data.TryGetProperty("evidence_kind", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == kind ||
            kind == "weather" && data.TryGetProperty("weather", out _));
    public static bool IsAmbiguous(IEnumerable<JsonElement> messages) => Data(messages)
        .FirstOrDefault().ValueKind == JsonValueKind.Object && Data(messages).First()
            .TryGetProperty("status", out var status) && status.GetString() == "ambiguous";
    public static bool HasReference(IEnumerable<JsonElement> messages) => Data(messages)
        .Any(data => data.TryGetProperty("evidence_kind", out var kind) && kind.GetString() == "reference");
}
