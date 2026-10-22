using System.Text.Json;
using IntellectBot.Validation;
using Xunit;

namespace IntellectBot.Tests;

public sealed class SchemaBudgetTests
{
    [Fact]
    public void BranchingRecursiveSchemaFailsWithinFiniteEvaluationBudget()
    {
        using var schema = JsonDocument.Parse("""{"anyOf":[{"$ref":"#"},{"$ref":"#"}]}""");
        using var value = JsonDocument.Parse("{}");

        Assert.False(JsonSchemaValidator.Matches(value.RootElement, schema.RootElement));
    }

    [Fact]
    public void ExhaustedBudgetCannotTurnNegationIntoSuccessfulValidation()
    {
        using var schema = JsonDocument.Parse("""{"not":{"anyOf":[{"$ref":"#/not"},{"$ref":"#/not"}]}}""");
        using var value = JsonDocument.Parse("{}");

        Assert.False(JsonSchemaValidator.Matches(value.RootElement, schema.RootElement));
    }
}
