using System.Net;
using System.Text.Json;
using IntellectBot.Configuration;
using IntellectBot.Domain;
using IntellectBot.Llm;
using Xunit;

namespace IntellectBot.Tests;

public sealed class LlmTests
{
    private const string Good = """{"choices":[{"finish_reason":"stop","message":{"content":" ответ "}}],"usage":{"prompt_tokens":12,"completion_tokens":7}}""";
    [Theory]
    [InlineData("qwen/qwen3.8-27b", true)]
    [InlineData("openai/gpt-oss-120b", false)]
    public async Task SendsActualParametersAndHidesReasoning(string model, bool qwen)
    {
        var values = Fixtures.Values();
        values["LLM_MODEL"] = model;
        var settings = AppSettings.FromValues(values);
        using var http = new HttpClient(new StubHttp(async (request, ct) =>
        {
            Assert.Equal("https://llm.example/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = json.RootElement;
            Assert.Equal(model, body.GetProperty("model").GetString());
            Assert.Equal(0.3, body.GetProperty("temperature").GetDouble());
            Assert.Equal("low", body.GetProperty("reasoning_effort").GetString());
            Assert.Equal(4096, body.GetProperty("max_completion_tokens").GetInt32());
            Assert.Equal("system", body.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal("question", body.GetProperty("messages")[1].GetProperty("content").GetString());
            if (qwen)
            {
                Assert.Equal("hidden", body.GetProperty("reasoning_format").GetString());
                Assert.False(body.TryGetProperty("include_reasoning", out _));
            }
            else
            {
                Assert.False(body.GetProperty("include_reasoning").GetBoolean());
                Assert.False(body.TryGetProperty("reasoning_format", out _));
            }
            return StubHttp.Json(Good);
        }));
        var result = await new LlmClient(http, settings.Llm, Fixtures.Log).GenerateAsync([new("system", "prompt"), new("user", "question")], .3, default);
        Assert.Equal(" ответ ", result.Text);
        Assert.Equal(12, result.PromptTokens);
        Assert.Equal(7, result.CompletionTokens);
    }
    [Theory]
    [InlineData(401, "authorization")]
    [InlineData(403, "authorization")]
    [InlineData(429, "rate_limit")]
    [InlineData(500, "unavailable")]
    [InlineData(302, "unavailable")]
    public async Task HttpErrorsAreSafe(int status, string code)
    {
        using var http = new HttpClient(new StubHttp((_, _) => Task.FromResult(StubHttp.Json("secret body", (HttpStatusCode)status))));
        var error = await Assert.ThrowsAsync<LlmException>(() => new LlmClient(http, Fixtures.Settings.Llm, Fixtures.Log).GenerateAsync([], .3, default));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("secret", error.Message);
    }
    [Theory]
    [InlineData("{}", "invalid_response")]
    [InlineData("null", "invalid_response")]
    [InlineData("{\"choices\":[]}", "invalid_response")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\"}]}", "incomplete_response")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"tool_calls\"}]}", "invalid_response")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null}}]}", "empty_response")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"  \"}}]}", "empty_response")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":42}}]}", "invalid_response")]
    public void InvalidResponsesAreClassified(string json, string code)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(code, Assert.Throws<LlmException>(() => LlmClient.Parse(doc.RootElement)).Code);
    }
    [Theory]
    [InlineData("true")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"12\"")]
    [InlineData("null")]
    public void InvalidUsageIsIgnored(string count)
    {
        using var doc = JsonDocument.Parse(Good.Replace("\"prompt_tokens\":12", "\"prompt_tokens\":" + count));
        Assert.Null(LlmClient.Parse(doc.RootElement).PromptTokens);
    }
    [Theory]
    [InlineData("network", "unavailable")]
    [InlineData("timeout", "timeout")]
    [InlineData("json", "invalid_response")]
    [InlineData("content", "invalid_response")]
    public async Task TransportErrorsAreMapped(string kind, string code)
    {
        using var http = new HttpClient(new StubHttp((_, _) => kind switch
        {
            "network" => throw new HttpRequestException("secret"),
            "timeout" => throw new TaskCanceledException("secret"),
            "json" => Task.FromResult(StubHttp.Json("not json")),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Good) })
        }));
        Assert.Equal(code, (await Assert.ThrowsAsync<LlmException>(() => new LlmClient(http, Fixtures.Settings.Llm, Fixtures.Log).GenerateAsync([], .3, default))).Code);
    }
    [Fact]
    public async Task ShutdownCancellationIsNotAUserTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = new HttpClient(new StubHttp((_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(StubHttp.Json(Good)); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LlmClient(http, Fixtures.Settings.Llm, Fixtures.Log).GenerateAsync([], .3, cancellation.Token));
    }
    [Fact]
    public async Task InvalidTemperatureMakesNoHttpRequest()
    {
        var called = false;
        using var http = new HttpClient(new StubHttp((_, _) => { called = true; return Task.FromResult(StubHttp.Json(Good)); }));
        Assert.Equal("invalid_temperature", (await Assert.ThrowsAsync<LlmException>(() => new LlmClient(http, Fixtures.Settings.Llm, Fixtures.Log).GenerateAsync([], .2, default))).Code);
        Assert.False(called);
    }
}
