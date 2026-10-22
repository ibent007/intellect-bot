using System.Text.Json;
using IntellectBot.Agent;
using IntellectBot.Application;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;
using IntellectBot.Llm;
using IntellectBot.Mcp;
using Xunit;
using static IntellectBot.Tests.AgentTestSupport;

namespace IntellectBot.Tests;

public sealed class AgentContinuationTests
{
    private sealed class ResumeLlm(bool routeFirst = true, AgentModelTurn? final = null) : IAgentLlmClient
    {
        private int count;
        public IReadOnlyList<JsonElement>? FinalMessages
        {
            get; private set;
        }
        public Task<AgentModelTurn> CompleteAsync(IReadOnlyList<JsonElement> messages,
            IReadOnlyList<McpToolDefinition> tools, double temperature, bool allowTools,
            CancellationToken ct, string? model = null)
        {
            count++;
            if (routeFirst && count == 1)
                return Task.FromResult(Calls(Call("weather", "get_weather", new
                {
                    city = "Москва"
                })));
            if (count == (routeFirst ? 2 : 1))
                throw new LlmException("rate_limit");
            FinalMessages = messages.ToArray();
            return Task.FromResult(final ?? Answer("В Москве 10 °C. Источник: Open-Meteo."));
        }
    }

    [Fact]
    public async Task SameQuestionResumesObtainedMcpResultInsteadOfCallingMcpAgain()
    {
        var clock = new AgentClock();
        var interaction = new InteractionState(clock);
        var llm = new ResumeLlm();
        var mcp = new RecordingGateway
        {
            Outcome = new(true, Json(new
            {
                status = "ok",
                weather = new
                {
                    temperature_c = 10,
                    source = "Open-Meteo"
                }
            }), null)
        };
        var engine = new AgentEngine(llm, () => mcp, new MemoryAgentState(),
            new MemoryReminderPreparation(), Fixtures.Settings.Llm, clock, interaction);

        await Assert.ThrowsAsync<LlmException>(() =>
            engine.ReplyAsync(42, 1, "Погода в Москве?", new(), [], default));
        var reply = await engine.ReplyAsync(42, 2, "Погода в Москве?", new(), [], default);

        Assert.Contains("10 °C", reply.Text);
        Assert.Single(mcp.Calls);
        Assert.Contains(llm.FinalMessages!, message => message.GetProperty("role").GetString() == "tool" &&
            message.GetProperty("content").GetString()!.Contains("temperature_c"));
    }

    [Fact]
    public async Task RetryAfterAmbiguousResultRetainsSourceIdsForClarification()
    {
        var clock = new AgentClock();
        var clarification = Answer("Какой город выбрать?") with
        {
            Decision = "clarify",
            Choices = [new AgentChoice(524901, "Москва, Россия")]
        };
        var llm = new ResumeLlm(final: clarification);
        var mcp = new RecordingGateway
        {
            Outcome = new(true, Json(new
            {
                status = "ambiguous",
                candidates = new[] { new { id = 524901, name = "Москва", country = "Россия" } }
            }), null)
        };
        var engine = new AgentEngine(llm, () => mcp, new MemoryAgentState(),
            new MemoryReminderPreparation(), Fixtures.Settings.Llm, clock, new InteractionState(clock));

        await Assert.ThrowsAsync<LlmException>(() =>
            engine.ReplyAsync(42, 1, "Погода в Москве?", new(), [], default));
        var reply = await engine.ReplyAsync(42, 2, "Погода в Москве?", new(), [], default);

        Assert.Contains("1. Москва, Россия", reply.Text);
        Assert.Single(mcp.Calls);
    }

    [Fact]
    public async Task RepeatedNumericSelectionResumesAfterQuotaFailure()
    {
        var clock = new AgentClock();
        var interaction = new InteractionState(clock);
        interaction.OfferChoices(42, [new ToolChoice("Москва", 524901, "Москва, Россия")]);
        var mcp = new RecordingGateway
        {
            Outcome = new(true, Json(new
            {
                status = "ok",
                weather = new
                {
                    temperature_c = 10
                }
            }), null)
        };
        var llm = new ResumeLlm(routeFirst: false);
        var engine = new AgentEngine(llm, () => mcp, new MemoryAgentState(),
            new MemoryReminderPreparation(), Fixtures.Settings.Llm, clock, interaction);

        await Assert.ThrowsAsync<LlmException>(() =>
            engine.ReplyAsync(42, 1, "1", new(), [], default));
        var reply = await engine.ReplyAsync(42, 2, "один", new(), [], default);

        Assert.Contains("10 °C", reply.Text);
        Assert.Equal("524901", Assert.Single(mcp.Calls).Args.GetProperty("locationId").GetString());
    }

    [Fact]
    public void PendingResultCannotBeReadByAnotherOwnerOrUnrelatedQuestion()
    {
        var interaction = new InteractionState(new AgentClock());
        interaction.SavePendingReply(42, "weather:1",
            [Json(new { role = "user", content = "Погода" })], []);

        Assert.Null(interaction.TakePendingReply(99, "weather:1"));
        Assert.Null(interaction.TakePendingReply(42, "another-question"));
        Assert.Null(interaction.TakePendingReply(42, "weather:1"));
    }

    [Fact]
    public async Task NumericChoiceSendsSelectedResultWithoutRepeatingCandidateList()
    {
        var clock = new AgentClock();
        var interaction = new InteractionState(clock);
        interaction.OfferChoices(42, [new ToolChoice("Москва", 524901, "Москва, Россия")]);
        interaction.RememberAgentContext(42,
            [Json(new { role = "user", content = "Погода?" }),
             Json(new { role = "assistant", content = "Большой прежний список кандидатов" })]);
        var mcp = new RecordingGateway
        {
            Outcome = new(true, Json(new
            {
                status = "ok",
                weather = new
                {
                    temperature_c = 10
                }
            }), null)
        };
        var llm = new ScriptedAgentLlm(Answer("В Москве 10 °C."));
        var engine = new AgentEngine(llm, () => mcp, new MemoryAgentState(),
            new MemoryReminderPreparation(), Fixtures.Settings.Llm, clock, interaction);

        await engine.ReplyAsync(42, 1, "один", new(), [], default);

        Assert.Equal("524901", Assert.Single(mcp.Calls).Args.GetProperty("locationId").GetString());
        var request = Assert.Single(llm.Requests);
        Assert.DoesNotContain(request.Messages, message =>
            message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String &&
            content.GetString()!.Contains("Большой прежний список"));
        Assert.Single(request.Messages, message => message.GetProperty("role").GetString() == "tool");
    }

    [Fact]
    public void ToolResultJsonKeepsRussianTextWithoutEscapedUnicode()
    {
        var compact = JsonDefaults.Text(new
        {
            name = "Санкт-Петербург",
            region = "Россия"
        });
        var escaped = JsonSerializer.Serialize(new
        {
            name = "Санкт-Петербург",
            region = "Россия"
        });

        Assert.Contains("Санкт-Петербург", compact);
        Assert.DoesNotContain("\\u", compact);
        Assert.True(compact.Length < escaped.Length / 2);
    }
}
