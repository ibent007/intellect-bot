using System.Text.Json;
using IntellectBot.Domain;
using IntellectBot.Mcp;
using IntellectBot.Validation;

namespace IntellectBot.Agent;

// Структурные ограничения задаёт MCP-сервер; предметные правила остаются в его методах.
public static class AgentInput
{
    public static JsonElement Validate(McpToolDefinition tool, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !JsonSchemaValidator.Matches(value, tool.InputSchema))
                throw Invalid();
            return value.Clone();
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static ReminderException Invalid() => new("invalid_arguments", "Параметры не соответствуют схеме инструмента. Уточните запрос.");
}
