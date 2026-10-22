using IntellectBot.Agent;
using IntellectBot.Domain;
using Xunit;
using static IntellectBot.Tests.AgentTestSupport;

namespace IntellectBot.Tests;

public sealed class ReminderRequestGuardTests
{
    [Theory]
    [InlineData("Напомни сегодня в 18 пойти к врачу")]
    [InlineData("Напомни сегодня в 18.00 пойти к врачу")]
    [InlineData("Добавь напоминание сегодня в 18:00 пойти к врачу")]
    public async Task ExplicitPastTodayInTokyoExplainsProblemWithoutPreparingOrCallingModel(string question)
    {
        var llm = new ScriptedAgentLlm();
        var state = new MemoryAgentState { Zone = "Asia/Tokyo" };
        var reminders = new MemoryReminderPreparation();
        var engine = new AgentEngine(llm, () => new RecordingGateway(), state, reminders, Fixtures.Settings.Llm, new AgentClock());
        var result = await engine.ReplyAsync(42, 1, question, new(), [], default);
        Assert.Contains("уже прошло", result.Text);
        Assert.Contains("Asia/Tokyo", result.Text);
        Assert.Empty(llm.Requests);
        Assert.Empty(reminders.Calls);
        Assert.Equal("clarify", state.Last!.Action);
    }

    [Fact]
    public void SameInstantCanBePastInTokyoAndFutureInMoscow()
    {
        var instant = new AgentClock().GetUtcNow();
        Assert.NotNull(ReminderRequestGuard.PastTimeClarification("Напомни сегодня в 18", "Asia/Tokyo", instant));
        Assert.Null(ReminderRequestGuard.PastTimeClarification("Напомни сегодня в 18", "Europe/Moscow", instant));
    }

    [Theory]
    [InlineData("Напомни сегодня в 18 пойти к врачу", "Сегодня в 18:00", true)]
    [InlineData("Кто такой Дуров?", "Дуров", true)]
    [InlineData("Что такое кортеж?", "Кортеж - упорядоченная коллекция.", false)]
    [InlineData("Привет", "Привет!", false)]
    public void EchoPolicyDistinguishesIncompleteAnswers(string question, string answer, bool expected) =>
        Assert.Equal(expected, AgentAnswerPolicy.IsEcho(question, answer));
}
