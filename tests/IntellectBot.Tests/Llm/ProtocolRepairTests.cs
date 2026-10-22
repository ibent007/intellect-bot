using System.Net;
using IntellectBot.Agent;
using IntellectBot.Llm;
using Xunit;

namespace IntellectBot.Tests;

public sealed class ProtocolRepairTests
{
    [Fact]
    public async Task ToolUseFailedGetsOneProtocolRepairBeforeAnyToolRuns()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttp((_, _) => Task.FromResult(++calls == 1
            ? StubHttp.Json("{\"error\":{\"code\":\"tool_use_failed\",\"message\":\"secret body\"}}", HttpStatusCode.BadRequest)
            : StubHttp.Json("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"Обычный корректный ответ.\"}}]}"))));
        var turn = await new AgentLlmClient(http, Fixtures.Settings.Llm, Fixtures.Log).CompleteAsync(
            [AgentTestSupport.Json(new { role = "user", content = "Объясни кортеж" })], AgentTestSupport.Tools, .7, true, default);
        Assert.Equal("Обычный корректный ответ.", turn.Text);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExperimentDoesNotRepairModelDecisionOrChangeRawMeasurement()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttp((_, _) =>
        {
            calls++;
            return Task.FromResult(StubHttp.Json("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"Кортеж\"}}]}"));
        }));
        var turn = await new AgentLlmClient(http, Fixtures.Settings.Llm, Fixtures.Log, generateResponse: false).CompleteAsync(
            [AgentTestSupport.Json(new { role = "user", content = "Что такое кортеж?" })], AgentTestSupport.Tools, .7, true, default);
        Assert.Equal("Кортеж", turn.Text);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ARegularBadRequestDoesNotTriggerProtocolRepair()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttp((_, _) =>
        {
            calls++;
            return Task.FromResult(StubHttp.Json("{\"error\":{\"code\":\"invalid_request_error\"}}", HttpStatusCode.BadRequest));
        }));
        var error = await Assert.ThrowsAsync<LlmException>(() => new AgentLlmClient(http, Fixtures.Settings.Llm, Fixtures.Log).CompleteAsync(
            [AgentTestSupport.Json(new { role = "user", content = "Привет" })], AgentTestSupport.Tools, .7, true, default));
        Assert.Equal("request_rejected", error.Code);
        Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData("Какая погода в Москве?", 1024)]
    [InlineData("Кто такой Николай Дуров?", 1024)]
    [InlineData("Напомни завтра в 18 проверить код", 1024)]
    [InlineData("Объясни SOLID и GRASP с примерами кода", 4096)]
    public async Task RoutingUsesCompactBudgetWithoutTruncatingOrdinaryExplanations(string question, int expected)
    {
        using var http = new HttpClient(new StubHttp(async (request, ct) =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(expected, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
            return StubHttp.Json("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"Корректный ответ для проверки транспорта.\"}}]}");
        }));
        await new AgentLlmClient(http, Fixtures.Settings.Llm, Fixtures.Log).CompleteAsync(
            [AgentTestSupport.Json(new { role = "user", content = question })], AgentTestSupport.Tools, .7, true, default);
    }

}
