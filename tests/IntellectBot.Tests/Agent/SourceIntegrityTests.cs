using IntellectBot.Agent;
using IntellectBot.Application;
using Xunit;
using static IntellectBot.Tests.AgentTestSupport;

namespace IntellectBot.Tests;

public sealed class SourceIntegrityTests
{
    [Fact]
    public async Task WeatherResultReturnsToModelAndUnsupportedNumbersAreNotPublished()
    {
        var clock = new UiClock();
        var mcp = new RecordingGateway
        {
            Outcome = new(true, Json(new
            {
                status = "ok",
                evidence_kind = "weather",
                public_text = "Температура: 5 °C. Источник: Open-Meteo."
            }), null)
        };
        var llm = new ScriptedAgentLlm(Calls(Call("weather", "get_weather", new
        {
            city = "Москва"
        })),
            Answer("В Москве 99 °C, источник — выдумка."));
        var engine = new AgentEngine(llm, () => mcp, new MemoryAgentState(), new MemoryReminderPreparation(), Fixtures.Settings.Llm, clock);
        var answer = await engine.ReplyAsync(42, 1, "Какая погода в Москве?", new(), [], default);
        Assert.Equal("Температура: 5 °C. Источник: Open-Meteo.", answer.Text);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(llm.Requests[1].Messages, message => message.GetProperty("role").GetString() == "tool" && message.GetRawText().Contains("5 °C"));
    }

    [Fact]
    public void ModelCannotRelabelRealCityAsInventedCountry()
    {
        var state = new InteractionState(new UiClock());
        state.RememberToolSource(42, "get_weather", Json(new
        {
            city = "Москва"
        }), Json(new
        {
            candidates = new[]
        {
            new { id = 10, name = "Москва", country = "Россия", admin1 = "Москва" },
            new { id = 20, name = "Москва", country = "США", admin1 = "Айдахо" }
        }
        }));
        var offered = state.OfferModelChoices(42, [new AgentChoice(10, "Москва, Монтсеррат"), new AgentChoice(20, "Москва, Монтсеррат")]);
        Assert.NotNull(offered);
        Assert.Contains("1. Москва, Россия", offered);
        Assert.Contains("2. Москва, США, Айдахо", offered);
        Assert.DoesNotContain("Монтсеррат", offered);
    }

    [Fact]
    public void SelectionMustBeIssuedBySourceAndBelongToCurrentOwner()
    {
        var state = new InteractionState(new UiClock());
        var args = Json(new
        {
            city = "Москва, RU",
            locationId = "10"
        });
        state.RememberToolSource(42, "get_weather", Json(new
        {
            city = "Москва"
        }),
            Json(new
            {
                candidates = new[] { new { id = 10, selection_arguments = args } }
            }));
        Assert.True(state.AcceptsSelection(42, "get_weather", args));
        Assert.False(state.AcceptsSelection(99, "get_weather", args));
        Assert.False(state.AcceptsSelection(42, "get_weather", Json(new
        {
            city = "Москва, US",
            locationId = "10"
        })));
        Assert.False(state.AcceptsSelection(42, "get_weather", Json(new
        {
            city = "Москва, RU",
            locationId = "999"
        })));
    }

    [Theory]
    [InlineData("экосистема вビジネスе")]
    [InlineData("карisma")]
    [InlineData("переименоval")]
    public void MixedScriptWordsAreRejected(string value) => Assert.True(AgentAnswerPolicy.HasMixedScriptWords(value));

    [Theory]
    [InlineData("Microsoft создала Windows.")]
    [InlineData("Пример: ```csharp\nvar карisma = 1;\n```")]
    public void ProperNamesAndCodeArePreserved(string value) => Assert.False(AgentAnswerPolicy.HasMixedScriptWords(value));
}
