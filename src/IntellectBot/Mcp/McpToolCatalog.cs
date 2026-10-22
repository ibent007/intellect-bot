using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntellectBot.Validation;

namespace IntellectBot.Mcp;

// Неизменяемый снимок tools/list. Набор методов и их права объявляет сервер.
public sealed class McpToolCatalog
{
    private readonly FrozenDictionary<string, McpToolDefinition> definitions;
    public IReadOnlyList<McpToolDefinition> Definitions
    {
        get;
    }

    public McpToolCatalog(IEnumerable<McpToolDefinition> tools)
    {
        var entries = tools.Select(tool => tool with
        {
            InputSchema = tool.InputSchema.Clone(),
            OutputSchema = tool.OutputSchema.Clone()
        }).ToArray();
        if (entries.Length > 128 || entries.Any(tool =>
            !Regex.IsMatch(tool.Name, @"^[A-Za-z0-9_.-]{1,100}$") ||
            !JsonSchemaValidator.IsSchema(tool.InputSchema) || !JsonSchemaValidator.IsSchema(tool.OutputSchema)) ||
            entries.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new ArgumentException("MCP-сервер вернул некорректный каталог инструментов.");
        definitions = entries.ToFrozenDictionary(tool => tool.Name, StringComparer.Ordinal);
        Definitions = Array.AsReadOnly(entries);
    }

    public McpToolDefinition? Find(string name) => definitions.GetValueOrDefault(name);

    public bool AcceptsArguments(string name, JsonElement value) =>
        Find(name) is { } tool && value.ValueKind == JsonValueKind.Object && JsonSchemaValidator.Matches(value, tool.InputSchema);

    public bool AcceptsResult(string name, JsonElement value) =>
        Find(name) is { } tool && JsonSchemaValidator.Matches(value, tool.OutputSchema);
}
