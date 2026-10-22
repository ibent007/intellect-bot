using System.Text.Json.Serialization;

namespace IntellectBot.Domain;

public sealed record ToolAuditEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments_summary")] string ArgumentsSummary,
    [property: JsonPropertyName("validation")] string Validation,
    [property: JsonPropertyName("execution")] string Execution)
{
    public override string ToString() => "ToolAuditEntry([hidden])";
}

