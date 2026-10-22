using IntellectBot.Validation;
using System.Text.Json;
using IntellectBot.Agent;
using IntellectBot.Domain;
using IntellectBot.Llm;
using IntellectBot.Telegram;
using Xunit;
using static IntellectBot.Tests.AgentTestSupport;
using IntellectBot.Mcp;

namespace IntellectBot.Tests;

public sealed class AgentToolProtocolTests
{
    private static AgentModelTurn Clarify(object args) => Calls(Call("clarify", AgentToolProtocol.ClarificationName(Tools), args));

    [Fact]
    public void NativeReadCallPreservesDiscoveredNameAndArguments()
    {
        var native = Calls(Call("decision", "get_schedule", new
        {
            date = "2026-10-12"
        }));
        var parsed = AgentToolProtocol.Interpret(native, Tools, true);
        Assert.Equal("get_schedule", Assert.Single(parsed.Calls).Name);
        Assert.Equal(native.Message.GetRawText(), parsed.Message.GetRawText());
    }

    [Fact]
    public void PlainAnswerDoesNotExecuteTools()
    {
        var parsed = AgentToolProtocol.Interpret(Answer("Объяснение с примером."), Tools, true);
        Assert.Empty(parsed.Calls);
        Assert.Equal("respond", parsed.Decision);
    }

    [Fact]
    public void ClarificationUsesExplicitActionAndNeverExecutesMcp()
    {
        var parsed = AgentToolProtocol.Interpret(Clarify(new
        {
            text = "На какую дату?"
        }), Tools, true);
        Assert.Equal("clarify", parsed.Decision);
        Assert.Empty(parsed.Calls);
    }

    [Fact]
    public void UndeclaredToolAndExtraClarificationFieldsAreRejected()
    {
        Assert.Throws<LlmException>(() => AgentToolProtocol.Interpret(Calls(Call("x", "shell", new { })), Tools, true));
        Assert.Throws<LlmException>(() => AgentToolProtocol.Interpret(Clarify(new { text = "Вопрос", owner = 42 }), Tools, true));
    }

    [Fact]
    public async Task ProductionLlmPathForExactReminderRequestShowsButtons()
    {
        JsonElement requestBody = default;
        var choice = new
        {
            choices = new[] { new { finish_reason = "tool_calls", message = new
            {
                tool_calls = new[] { new { id = "decision_1", type = "function", function = new
                {
                    name = "add_reminder",
                    arguments = JsonSerializer.Serialize(new { text = "Проверить код", remind_at = "2030-01-15T18:00:00+03:00" })
                } } }
            } } }
        };
        using var http = new HttpClient(new StubHttp(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            requestBody = document.RootElement.Clone();
            return StubHttp.Json(JsonSerializer.Serialize(choice));
        }));
        var state = new MemoryAgentState();
        var preparation = new MemoryReminderPreparation();
        var engine = new AgentEngine(new AgentLlmClient(http, Fixtures.Settings.Llm, Fixtures.Log),
            () => new RecordingGateway(), state, preparation, Fixtures.Settings.Llm, new AgentClock());
        var dialogue = new DialogueService(new MemoryStore(), new FakeLlm(), Fixtures.Settings.Llm, engine);
        var api = new FakeTelegram();
        await new UpdateHandler(api, dialogue, Fixtures.Log).HandleAsync(Fixtures.Message(
            "Создай напоминание Проверить код на 2030-01-15\n в 18:00 по моему часовому поясу."), CancellationToken.None);
        Assert.Equal("auto", requestBody.GetProperty("tool_choice").GetString());
        var message = Assert.Single(api.Messages);
        Assert.Contains("Проверить код", message.Text);
        Assert.Equal(2, message.Keyboard!.Rows.SelectMany(row => row).Count());
        Assert.Single(preparation.Calls);
    }

    [Fact]
    public void ReminderArgumentsSchemaRequiresTimeAndRejectsExtraOwner()
    {
        var schema = Tools.Single(tool => tool.Name == "add_reminder").InputSchema;

        Assert.False(JsonSchemaValidator.Matches(
            Json(new
            {
                text = "Проверить код"
            }), schema));

        Assert.True(JsonSchemaValidator.Matches(Json(new
        {
            text = "Проверить код",
            remind_at = "2030-01-15T18:00:00+03:00"
        }), schema));

        Assert.False(JsonSchemaValidator.Matches(Json(new
        {
            text = "Проверить код",
            remind_at = "2030-01-15T18:00:00+03:00",
            owner = 42
        }), schema));
    }
}
