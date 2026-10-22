using System.Text.Json;
using IntellectBot.Validation;
using Xunit;

namespace IntellectBot.Tests;

public sealed class JsonSchemaValidatorTests
{
    [Theory]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":false}", "{\"unexpected\":1}", false)]
    [InlineData("{\"type\":\"array\",\"uniqueItems\":true,\"items\":{\"type\":\"integer\"}}", "[1,1]", false)]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"boolean\"}}", "[true,false]", true)]
    [InlineData("{\"type\":\"number\",\"exclusiveMinimum\":0,\"multipleOf\":0.5}", "1.5", true)]
    [InlineData("{\"type\":\"number\",\"exclusiveMinimum\":0,\"multipleOf\":0.5}", "0", false)]
    [InlineData("{\"type\":[\"string\",\"null\"]}", "null", true)]
    [InlineData("{\"type\":\"string\",\"pattern\":\"^A[0-9]+$\"}", "\"A42\"", true)]
    [InlineData("{\"type\":\"string\",\"format\":\"date\"}", "\"2026-02-30\"", false)]
    [InlineData("{\"type\":\"string\",\"format\":\"date-time\"}", "\"2030-01-15T18:00:00\"", false)]
    [InlineData("{\"type\":\"string\",\"format\":\"date-time\"}", "\"2030-01-15T18:00:00+09:00\"", true)]
    [InlineData("{\"not\":{\"const\":0}}", "0", false)]
    [InlineData("{\"type\":\"array\",\"contains\":{\"const\":5},\"minContains\":2}", "[5,1,5]", true)]
    [InlineData("{\"type\":\"object\",\"dependentRequired\":{\"card\":[\"address\"]}}", "{\"card\":1}", false)]
    [InlineData("{\"type\":\"object\",\"patternProperties\":{\"^x-\":{\"type\":\"number\"}},\"additionalProperties\":false}", "{\"x-count\":3}", true)]
    [InlineData("true", "{\"a\":{\"x\":1,\"x\":2}}", false)]
    public void SameValidatorHandlesInputsAndOutputs(string schema, string json, bool expected)
    {
        using var definition = JsonDocument.Parse(schema);
        using var value = JsonDocument.Parse(json);
        Assert.Equal(expected, JsonSchemaValidator.Matches(value.RootElement, definition.RootElement));
    }

    [Fact]
    public void SchemaBranchesUseOriginalRootForReferences()
    {
        using var schema = JsonDocument.Parse("""{"anyOf":[{"$ref":"#/$defs/choice"}],"$defs":{"choice":{"type":"integer","minimum":2}}}""");
        using var value = JsonDocument.Parse("3");
        Assert.True(JsonSchemaValidator.Matches(value.RootElement, schema.RootElement));
    }
    [Theory]
    [InlineData("{\"unevaluatedProperties\":false}")]
    [InlineData("{\"$dynamicRef\":\"#node\"}")]
    public void UnsupportedConstraintsFailClosed(string schema)
    {
        using var definition = JsonDocument.Parse(schema);
        using var value = JsonDocument.Parse("{}");
        Assert.False(JsonSchemaValidator.Matches(value.RootElement, definition.RootElement));
    }
}
