using System.Text.Json;
using IntellectBot.Agent;
using IntellectBot.Domain;
using IntellectBot.Mcp;
using IntellectBot.Validation;
using Xunit;
using static IntellectBot.Tests.AgentTestSupport;

namespace IntellectBot.Tests;

public sealed class GenericToolTests
{
    [Fact]
    public async Task NewReadOnlyToolWithNestedTypedArgumentsNeedsNoAgentBranch()
    {
        var schema = Json(new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "numbers", "options" },
            properties = new
            {
                numbers = new
                {
                    type = "array",
                    minItems = 1,
                    items = new
                    {
                        type = "number"
                    }
                },
                options = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "enabled" },
                    properties = new
                    {
                        enabled = new
                        {
                            type = "boolean"
                        }
                    }
                }
            }
        });
        var method = new McpToolDefinition("calculate_total", "Складывает числа", schema, Json(new
        {
            type = "number"
        }), true);
        var mcp = new RecordingGateway { Catalog = new(new[] { method }), Outcome = new(true, Json(7.5), null) };
        var llm = new ScriptedAgentLlm(Calls(Call("sum", method.Name, new
        {
            numbers = new[] { 2, 5.5 },
            options = new
            {
                enabled = true
            }
        })), Answer("Сумма: 7,5."));
        var state = new MemoryAgentState();
        var engine = new AgentEngine(llm, () => mcp, state, new MemoryReminderPreparation(), Fixtures.Settings.Llm, new AgentClock());
        var response = await engine.ReplyAsync(42, 1, "Сложи числа", new(), [], default);
        Assert.Equal("Сумма: 7,5.", response.Text);
        Assert.Equal(method.Name, Assert.Single(mcp.Calls).Name);
        Assert.Contains(llm.Requests[1].Messages, message => message.GetRawText().Contains("7.5"));
        Assert.True(mcp.Catalog.AcceptsResult(method.Name, Json(7.5)));
        Assert.False(mcp.Catalog.AcceptsResult(method.Name, Json("wrong")));
    }

    [Fact]
    public async Task NewWriteToolIsNeverExecutedWithoutConfirmationAdapter()
    {
        var tool = Tool("delete_records") with
        {
            ReadOnly = false
        };
        var mcp = new RecordingGateway { Catalog = new(new[] { tool }) };
        var llm = new ScriptedAgentLlm(Calls(Call("write", tool.Name, new
        {
        })));
        var engine = new AgentEngine(llm, () => mcp, new MemoryAgentState(), new MemoryReminderPreparation(), Fixtures.Settings.Llm, new AgentClock());
        var result = await engine.ReplyAsync(42, 1, "Удали записи", new(), [], default);
        Assert.Empty(mcp.Calls);
        Assert.Contains("подтверждения", result.Text);
    }

    [Fact]
    public void MissingRequiredArrayInServerSchemaIsValidAndNotAssumedByDecisionBuilder()
    {
        var tool = new McpToolDefinition("ping", "Проверка", Json(new
        {
            type = "object",
            additionalProperties = false
        }), Json(true), true);
        var schema = Json(AgentToolProtocol.Definitions([tool], true))[0]
            .GetProperty("function").GetProperty("parameters");
        Assert.True(JsonSchemaValidator.Matches(Json(new
        {
        }), schema));
        Assert.NotNull(new McpToolCatalog(new[] { tool }).Find("ping"));
    }

    [Fact]
    public void ToolLocalReferencesAndSameFieldNamesKeepTheirOwnTypes()
    {
        var number = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{"$ref":"#/$defs/item"}},"required":["value"],"additionalProperties":false,"$defs":{"item":{"type":"number"}}}""");
        var text = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{"$ref":"#/$defs/item"}},"required":["value"],"additionalProperties":false,"$defs":{"item":{"type":"string"}}}""");
        var tools = new[] { new McpToolDefinition("numeric", "Число", number, Json(true), true), new McpToolDefinition("textual", "Текст", text, Json(true), true) };
        var definitions = Json(AgentToolProtocol.Definitions(tools, true));
        var schema = definitions[0].GetProperty("function").GetProperty("parameters");
        Assert.True(JsonSchemaValidator.Matches(Json(new
        {
            value = 5
        }), schema));
        Assert.False(JsonSchemaValidator.Matches(Json(new
        {
            value = 5
        }),
            definitions[1].GetProperty("function").GetProperty("parameters")));
        Assert.Equal(5, AgentInput.Validate(tools[0], "{\"value\":5}").GetProperty("value").GetInt32());
        Assert.Throws<ReminderException>(() => AgentInput.Validate(tools[1], "{\"value\":5}"));
    }

    [Fact]
    public void CatalogRejectsDuplicateNamesAndDoesNotInferWritePermissionFromName()
    {
        Assert.Throws<ArgumentException>(() => new McpToolCatalog(new[] { Tool("same"), Tool("same") }));
        var catalog = new McpToolCatalog(new[] { Tool("get_something") with { ReadOnly = false } });
        Assert.False(catalog.Find("get_something")!.ReadOnly);
    }
    [Fact]
    public void ReferenceLookingValuesInsideConstAreNotRewrittenAsSchemas()
    {
        var input = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{"const":{"$ref":"literal","$id":"data"}}},"required":["value"]}""");
        var method = new McpToolDefinition("constant", "Константа", input, Json(true), true);
        var schema = Json(AgentToolProtocol.Definitions([method], true))[0].GetProperty("function").GetProperty("parameters");
        var literal = schema.GetProperty("properties").GetProperty("value").GetProperty("const");
        Assert.Equal("literal", literal.GetProperty("$ref").GetString());
        Assert.Equal("data", literal.GetProperty("$id").GetString());
    }

    [Fact]
    public void ServerFilterPreservesExplicitExtensibleAndComposedContracts()
    {
        var open = Json(new
        {
            type = "object",
            properties = new
            {
            },
            additionalProperties = new
            {
                type = "number"
            }
        });
        var normalized = IntellectBot.McpServer.Hosting.ToolValidationFilter.InputSchema(open);
        Assert.True(JsonSchemaValidator.Matches(Json(new
        {
            value = 2
        }), normalized));
        Assert.False(JsonSchemaValidator.Matches(Json(new
        {
            value = "bad"
        }), normalized));
        var composed = JsonSerializer.Deserialize<JsonElement>("""{"allOf":[{"type":"object","properties":{"value":{"type":"number"}},"required":["value"]}]}""");
        Assert.True(JsonSchemaValidator.Matches(Json(new
        {
            value = 2
        }),
            IntellectBot.McpServer.Hosting.ToolValidationFilter.InputSchema(composed)));
    }
}
