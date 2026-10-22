using System.Text.Json;
using IntellectBot.Configuration;
using IntellectBot.Domain;

namespace IntellectBot.Agent;

// Удаляет только целые старые ходы. Пары assistant/tool остаются неразрывными.
internal static class AgentContextBudget
{
    public static void Fit(List<JsonElement> messages, LlmSettings settings)
    {
        var hasResult = messages.Any(message => message.GetProperty("role").GetString() == "tool");
        var limit = hasResult ? Math.Max(settings.ContextMaxChars, settings.ToolContextMaxChars) : settings.ContextMaxChars;
        int Size() => messages.Sum(message =>
            (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? ContextBuilder.Length(content.GetString()!) : 0) +
            (message.TryGetProperty("tool_calls", out var calls) ? ContextBuilder.Length(calls.GetRawText()) : 0));

        while (Size() > limit && messages.Count > 2)
        {
            var nextTurn = messages.FindIndex(2, message => message.GetProperty("role").GetString() == "user");
            if (nextTurn < 0)
                break;
            messages.RemoveRange(1, nextTurn - 1);
        }
        if (Size() > limit)
            throw new DialogueException("Результат запроса слишком большой. Уточните запрос и попробуйте ещё раз.");
    }
}
