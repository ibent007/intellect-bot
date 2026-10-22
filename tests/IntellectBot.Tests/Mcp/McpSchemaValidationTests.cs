using IntellectBot.Validation;
using System.Text.Json;
using IntellectBot.Mcp;
using Xunit;

namespace IntellectBot.Tests;

public sealed class McpSchemaValidationTests
{
    private const string Schema = """
        {"type":"object","required":["date","lessons"],"additionalProperties":false,
         "properties":{"date":{"type":"string"},"lessons":{"type":"array","items":{"$ref":"#/$defs/lesson"}}},
         "$defs":{"lesson":{"type":"object","required":["title"],"properties":{"title":{"type":"string"}}}}}
        """;
    [Theory]
    [InlineData("{\"date\":\"2026-10-12\",\"lessons\":[]}", true)]
    [InlineData("{\"date\":\"2026-10-12\",\"lessons\":[{\"title\":\"Программирование\"}]}", true)]
    [InlineData("{\"date\":\"2026-10-12\"}", false)]
    [InlineData("{\"date\":42,\"lessons\":[]}", false)]
    [InlineData("{\"date\":\"2026-10-12\",\"lessons\":[{\"title\":42}]}", false)]
    [InlineData("{\"date\":\"2026-10-12\",\"lessons\":[],\"owner\":42}", false)]
    public void OutputValidationDetectsMissingFieldsAndNestedWrongTypes(string json, bool expected)
    {
        using var value = JsonDocument.Parse(json);
        using var schema = JsonDocument.Parse(Schema);
        Assert.Equal(expected, JsonSchemaValidator.Matches(value.RootElement, schema.RootElement));
    }
}
