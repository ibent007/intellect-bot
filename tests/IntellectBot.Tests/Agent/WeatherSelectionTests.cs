using IntellectBot.Agent;
using IntellectBot.Application;
using IntellectBot.Domain;
using Xunit;
using static IntellectBot.Tests.AgentTestSupport;

namespace IntellectBot.Tests;

public sealed class WeatherSelectionTests
{
    private static AgentModelTurn Clarify(params AgentChoice[] options) => Answer("Какой город вы имеете в виду?") with
    {
        Decision = "clarify",
        Choices = options
    };

    private static object Ambiguous(int count = 2) => new
    {
        status = "ambiguous",
        candidates = Enumerable.Range(1, count).Select(id => new
        {
            id,
            name = "Москва",
            country = "Россия",
            admin1 = "Регион " + id,
            admin2 = ""
        }).ToArray(),
        weather = (object?)null
    };

    [Fact]
    public async Task ModelReceivesRawCandidatesAndChoosesDisplayedList()
    {
        var clock = new UiClock();
        var interactions = new InteractionState(clock);
        var mcp = new RecordingGateway { Outcome = new(true, Json(Ambiguous()), null) };
        var llm = new ScriptedAgentLlm(Calls(Call("first", "get_weather", new
        {
            city = "Москва"
        })),
            Clarify(new AgentChoice(2, "Москва, Россия, Регион 2")));
        var engine = new AgentEngine(llm, () => mcp, new MemoryAgentState(), new MemoryReminderPreparation(), Fixtures.Settings.Llm, clock, interactions);
        var result = await engine.ReplyAsync(42, 1, "Погода в Москве?", new(), [], default);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(llm.Requests[1].Messages, message => message.GetRawText().Contains("candidates"));
        Assert.Contains("1. Москва, Россия, Регион 2", result.Text);
        Assert.DoesNotContain("Регион 1", result.Text);
        Assert.Single(mcp.Calls);
    }

    [Fact]
    public async Task FirstThenNinthOptionKeepsOriginalMappingAndReturnsBothResultsToModel()
    {
        var clock = new UiClock();
        var interactions = new InteractionState(clock);
        var mcp = new RecordingGateway { Outcome = new(true, Json(Ambiguous(10)), null) };
        var options = Enumerable.Range(1, 10).Select(id => new AgentChoice(id, "Москва, Регион " + id)).ToArray();
        var llm = new ScriptedAgentLlm(Calls(Call("first", "get_weather", new
        {
            city = "Москва"
        })),
            Clarify(options), Answer("Погода первого места: 10 °C."), Answer("Погода девятого места: 20 °C."));
        var state = new MemoryAgentState();
        var engine = new AgentEngine(llm, () => mcp, state, new MemoryReminderPreparation(), Fixtures.Settings.Llm, clock, interactions);
        await engine.ReplyAsync(42, 1, "Какая погода в Москве?", new(), [], default);
        mcp.Outcome = new(true, Json(new
        {
            status = "ok",
            weather = new
            {
                temperature_c = 10
            }
        }), null);
        var first = await engine.ReplyAsync(42, 2, "один", new(), [], default);
        mcp.Outcome = new(true, Json(new
        {
            status = "ok",
            weather = new
            {
                temperature_c = 20
            }
        }), null);
        var ninth = await engine.ReplyAsync(42, 3, "А в девятом варианте?", new(), [], default);
        Assert.Equal("1", mcp.Calls[1].Args.GetProperty("locationId").GetString());
        Assert.Equal("9", mcp.Calls[2].Args.GetProperty("locationId").GetString());
        Assert.Equal("Москва", mcp.Calls[2].Args.GetProperty("city").GetString());
        Assert.Contains("10 °C", first.Text);
        Assert.Contains("20 °C", ninth.Text);
        Assert.Equal(4, llm.Requests.Count);
        // Имена исторических function-вызовов должны совпадать с объявленным LLM-интерфейсом.
        foreach (var message in llm.Requests[3].Messages)
            if (message.TryGetProperty("tool_calls", out var calls))
                foreach (var call in calls.EnumerateArray())
                    if (call.GetProperty("id").GetString()!.StartsWith("selection_", StringComparison.Ordinal))
                        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Contains(llm.Requests[3].Messages, message => message.GetProperty("role").GetString() == "tool" &&
            message.GetProperty("content").GetString()!.Contains("\"temperature_c\":20", StringComparison.Ordinal));
        Assert.Equal("success", Assert.Single(state.Last!.Tools).Execution);
    }

    [Fact]
    public async Task FabricatedIdsAreNotShown()
    {
        var clock = new UiClock();
        var mcp = new RecordingGateway { Outcome = new(true, Json(Ambiguous()), null) };
        var options = new[] { new AgentChoice(99, "Нет такого города") };
        var llm = new ScriptedAgentLlm(Calls(Call("first", "get_weather", new
        {
            city = "Москва"
        })), Clarify(options));
        var engine = new AgentEngine(llm, () => mcp, new MemoryAgentState(), new MemoryReminderPreparation(), Fixtures.Settings.Llm, clock);
        var result = await engine.ReplyAsync(42, 1, "Погода?", new(), [], default);
        Assert.Contains("Уточните", result.Text);
        Assert.DoesNotContain("1.", result.Text);
    }

    [Fact]
    public void OptionsAreOwnedAndRemainAvailableForFollowUp()
    {
        var state = new InteractionState(new UiClock());
        state.OfferChoices(42, ["Москва, Россия", "Москва, США"]);
        Assert.Null(state.SelectChoice(99, "1").Topic);
        Assert.NotNull(state.SelectChoice(42, "999999999999999999999").Error);
        Assert.Equal("Москва, Россия", state.SelectChoice(42, "1").Topic);
        Assert.Equal("Москва, США", state.SelectChoice(42, "А во втором варианте?").Topic);
    }

    [Fact]
    public void ExpiryNewQuestionAndResetDoNotReuseOldOptions()
    {
        var clock = new UiClock();
        var state = new InteractionState(clock);
        state.OfferChoices(42, ["Москва"]);
        clock.Now = clock.Now.AddMinutes(6);
        Assert.Contains("устарел", state.SelectChoice(42, "1").Error);
        state.OfferChoices(42, ["Москва"]);
        Assert.Null(state.SelectChoice(42, "Что такое кортеж?").Topic);
        Assert.Null(state.SelectChoice(42, "1").Topic);
        state.OfferChoices(42, ["Москва"]);
        state.Clear(42);
        Assert.Null(state.SelectChoice(42, "1").Topic);
    }

    [Fact]
    public async Task OutOfRangeChoiceDoesNotCallMcpOrLlm()
    {
        var clock = new UiClock();
        var selections = new InteractionState(clock);
        selections.OfferChoices(42, ["Москва"]);
        var llm = new ScriptedAgentLlm();
        var gateway = new RecordingGateway();
        var engine = new AgentEngine(llm, () => gateway, new MemoryAgentState(), new MemoryReminderPreparation(), Fixtures.Settings.Llm, clock, selections);
        var result = await engine.ReplyAsync(42, 1, "5", new(), [], default);
        Assert.Contains("от 1 до 1", result.Text);
        Assert.Empty(llm.Requests);
        Assert.Empty(gateway.Calls);
    }
}
