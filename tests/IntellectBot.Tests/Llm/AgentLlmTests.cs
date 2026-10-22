using System.Net;
using System.Text.Json;
using IntellectBot.Llm;
using Xunit;

namespace IntellectBot.Tests;

public sealed class AgentLlmTests
{
    [Fact]
    public void NativeCallsAreParsedWithoutReasoning()
    {
        var result = AgentLlmClient.Parse(JsonSerializer.SerializeToElement(new
        {
            choices = new[] { new { finish_reason = "tool_calls", message = new
            {
                content = (string?)null, reasoning = "private reasoning",
                tool_calls = new[] { new { id = "call_1", type = "function",
                    function = new { name = "get_schedule", arguments = "{\"date\":\"2026-10-12\"}" } } }
            } } }
        }));
        Assert.Equal("get_schedule", Assert.Single(result.Calls).Name);
        Assert.DoesNotContain("reasoning", result.Message.GetRawText());
    }

    [Theory]
    [InlineData("qwen/qwen3.8-27b")]
    [InlineData("openai/gpt-oss-120b")]
    public async Task OrdinaryAnswerNeedsOneRequestOnEveryModel(string model)
    {
        var requests = 0;
        using var http = new HttpClient(new StubHttp((_, _) =>
        {
            requests++;
            return Task.FromResult(StubHttp.Json("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"Кортеж — упорядоченная коллекция. Пример: (1, 2).\"}}]}"));
        }));
        var client = new AgentLlmClient(http, Fixtures.Settings.Llm, Fixtures.Log);
        var result = await client.CompleteAsync(
            [AgentTestSupport.Json(new { role = "user", content = "Что такое кортеж?" })],
            AgentTestSupport.Tools, .7, true, default, model);
        Assert.Equal(1, requests);
        Assert.Contains("Пример", result.Text);
    }

    [Fact]
    public async Task ToolResultProducesFinalAnswerWithReducedCompletionBudget()
    {
        var count = 0;
        using var http = new HttpClient(new StubHttp(async (request, ct) =>
        {
            count++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(1024, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
            return StubHttp.Json("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"В Москве 10 °C. Источник: Open-Meteo.\"}}]}");
        }));
        var client = new AgentLlmClient(http, Fixtures.Settings.Llm, Fixtures.Log);
        var messages = new[]
        {
            AgentTestSupport.Json(new { role = "user", content = "Погода в Москве?" }),
            AgentTestSupport.Json(new { role = "tool", tool_call_id = "weather", content = "{\"temperature_c\":10}" })
        };
        var answer = await client.CompleteAsync(messages, AgentTestSupport.Tools, .7, true, default);
        Assert.Equal(1, count);
        Assert.Contains("10 °C", answer.Text);
    }

    [Theory]
    [InlineData("Сегодня")]
    [InlineData("Сегодня в 18:00")]
    public async Task EchoInsteadOfReminderClarificationGetsOnlyOneRepair(string text)
    {
        var count = 0;
        using var http = new HttpClient(new StubHttp((_, _) =>
        {
            count++;
            return Task.FromResult(StubHttp.Json(JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "tool_calls", message = new
                { tool_calls = new[] { new { id = "bad", type = "function", function = new
                    { name = "request_clarification", arguments = JsonSerializer.Serialize(new { text }) } } } } } }
            })));
        }));
        var client = new AgentLlmClient(http, Fixtures.Settings.Llm, Fixtures.Log);
        var error = await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync(
            [AgentTestSupport.Json(new { role = "user", content = "Напомни сегодня в 18 пойти к врачу" })],
            AgentTestSupport.Tools, 0.7, true, default));
        Assert.Equal("unhelpful_response", error.Code);
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("length", "incomplete_response")]
    [InlineData("content_filter", "invalid_response")]
    public void UnexpectedTerminationIsRejected(string reason, string code)
    {
        var error = Assert.Throws<LlmException>(() => AgentLlmClient.Parse(
            JsonSerializer.SerializeToElement(new
            {
                choices = new[] { new { finish_reason = reason, message = new { content = "text" } } }
            })));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void DuplicateCallIdsAreRejected()
    {
        var call = new
        {
            id = "same",
            type = "function",
            function = new
            {
                name = "get_schedule",
                arguments = "{}"
            }
        };
        Assert.Throws<LlmException>(() => AgentLlmClient.Parse(JsonSerializer.SerializeToElement(new
        {
            choices = new[] { new { finish_reason = "tool_calls", message = new { tool_calls = new[] { call, call } } } }
        })));
    }

    [Fact]
    public async Task HttpRequestUsesDiscoveredSchemaAndDisablesToolsAtLimit()
    {
        JsonElement body = default;
        using var http = new HttpClient(new StubHttp(async (request, ct) =>
        {
            using var parsed = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            body = parsed.RootElement.Clone();
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return StubHttp.Json("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"Итог\"}}]}");
        }));
        var client = new AgentLlmClient(http, Fixtures.Settings.Llm, Fixtures.Log);
        var result = await client.CompleteAsync(new[] { AgentTestSupport.Json(new { role = "user", content = "Запрос" }) },
            AgentTestSupport.Tools, 0.3, false, CancellationToken.None);
        Assert.Equal("Итог", result.Text);
        Assert.Equal("auto", body.GetProperty("tool_choice").GetString());
        Assert.Single(body.GetProperty("tools").EnumerateArray());
        Assert.Equal("hidden", body.GetProperty("reasoning_format").GetString());
        Assert.Equal("request_clarification", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.DoesNotContain("test-api-key", body.GetRawText());
    }

    [Theory]
    [InlineData(401, "authorization")]
    [InlineData(429, "rate_limit")]
    [InlineData(500, "unavailable")]
    public async Task HttpErrorsAreSafeAndNotRetried(int status, string code)
    {
        var count = 0;
        using var http = new HttpClient(new StubHttp((_, _) =>
        {
            count++;
            return Task.FromResult(StubHttp.Json("private provider error", (HttpStatusCode)status));
        }));
        var client = new AgentLlmClient(http, Fixtures.Settings.Llm, Fixtures.Log);
        var error = await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync(
            Array.Empty<JsonElement>(), AgentTestSupport.Tools, 0.3, true, CancellationToken.None));
        Assert.Equal(code, error.Code);
        Assert.Equal(1, count);
        Assert.DoesNotContain("private provider error", error.Message);
    }
}
