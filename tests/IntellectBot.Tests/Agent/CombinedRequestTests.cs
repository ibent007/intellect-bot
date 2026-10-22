using IntellectBot.Agent;
using IntellectBot.Domain;
using IntellectBot.Telegram;
using Xunit;
using static IntellectBot.Tests.AgentTestSupport;

namespace IntellectBot.Tests;

public sealed class CombinedRequestTests
{
    [Fact]
    public async Task ReadResultReachesModelAndAnswerKeepsReminderButtons()
    {
        var mcp = new RecordingGateway { Outcome = new(true, Json(new { temperature_c = 10 }), null) };
        var preparation = new MemoryReminderPreparation();
        var llm = new ScriptedAgentLlm(
            Calls(Call("weather", "get_weather", new
            {
                city = "Москва"
            }),
                Call("reminder", "add_reminder", new
                {
                    text = "Проверить код",
                    remind_at = "2030-01-15T18:00:00+03:00"
                })),
            Answer("В Москве 10 °C. Напоминание ожидает подтверждения."));
        var engine = new AgentEngine(llm, () => mcp, new MemoryAgentState(), preparation,
            Fixtures.Settings.Llm, new AgentClock());

        var api = new FakeTelegram();
        var dialogue = new DialogueService(new MemoryStore(), new FakeLlm(), Fixtures.Settings.Llm, engine);
        await new UpdateHandler(api, dialogue, Fixtures.Log).HandleAsync(Fixtures.Message("Погода и напоминание"), default);

        Assert.Contains(api.Messages, message => message.Text.Contains("10 °C"));
        Assert.Single(api.Messages, message => message.Keyboard is not null);
        Assert.Single(mcp.Calls);
        Assert.Single(preparation.Calls);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(llm.Requests[1].Messages, message => message.GetProperty("role").GetString() == "tool" &&
            message.GetProperty("content").GetString()!.Contains("temperature_c"));
    }
}
