using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IntellectBot.Llm;
using Xunit;

namespace IntellectBot.Tests;

public sealed class QuotaTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("7.66s", 7.66)]
    [InlineData("2m59.56s", 179.56)]
    [InlineData("1h2m3s", 3723)]
    [InlineData("10ms", .01)]
    public void ProviderResetDurationsAreParsed(string text, double seconds) =>
        Assert.Equal(seconds, LlmRequestCoordinator.Duration(text)!.Value.TotalSeconds, 3);

    [Theory]
    [InlineData("NaN")]
    [InlineData("-1s")]
    [InlineData("garbage7s")]
    [InlineData("90000s")]
    public void InvalidResetHeadersAreNotTrusted(string text) =>
        Assert.Null(LlmRequestCoordinator.Duration(text));

    [Fact]
    public async Task KnownTokenExhaustionWaitsWithoutSendingAnotherHttpRequest()
    {
        var clock = new Clock();
        var delays = new List<TimeSpan>();
        var coordinator = new LlmRequestCoordinator(clock, (delay, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            delays.Add(delay);
            clock.Now += delay;
            return Task.CompletedTask;
        });
        var window = coordinator.For("model-a");
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("x-ratelimit-remaining-tokens", "0");
        response.Headers.Add("x-ratelimit-reset-tokens", "7.5s");
        coordinator.Observe(window, response.Headers, false);

        await coordinator.WaitAsync(window, 500, TimeSpan.FromSeconds(60), default);

        Assert.Equal(TimeSpan.FromSeconds(7.5), Assert.Single(delays));
        Assert.NotSame(window, coordinator.For("model-b"));
    }

    [Fact]
    public async Task PositiveTokenBalanceDoesNotBlockRequestByAnEstimate()
    {
        var coordinator = new LlmRequestCoordinator(new Clock(), (_, _) => throw new InvalidOperationException("Не должно быть ожидания."));
        var window = coordinator.For("positive");
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("x-ratelimit-remaining-tokens", "20");
        response.Headers.Add("x-ratelimit-reset-tokens", "45s");
        coordinator.Observe(window, response.Headers, false);
        await coordinator.WaitAsync(window, 500, TimeSpan.FromSeconds(5), default);
    }

    [Fact]
    public async Task DailyLimitDoesNotCauseAnUnboundedWait()
    {
        var clock = new Clock();
        var coordinator = new LlmRequestCoordinator(clock, (_, _) => throw new InvalidOperationException());
        var window = coordinator.For("daily");
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("x-ratelimit-remaining-requests", "0");
        response.Headers.Add("x-ratelimit-reset-requests", "1h");
        coordinator.Observe(window, response.Headers, false);

        var error = await Assert.ThrowsAsync<LlmException>(() =>
            coordinator.WaitAsync(window, 100, TimeSpan.FromSeconds(60), default));

        Assert.Equal("rate_limit", error.Code);
        Assert.Equal(TimeSpan.FromHours(1), error.RetryAfter);
    }

    [Fact]
    public async Task RetryAfterIsObservedAndOnlyOneRetryIsAllowed()
    {
        var requests = 0;
        var clock = new Clock();
        var coordinator = new LlmRequestCoordinator(clock, (delay, _) =>
        {
            clock.Now += delay;
            return Task.CompletedTask;
        });
        using var http = new HttpClient(new StubHttp((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        }));
        var transport = new ChatCompletionTransport(http, Fixtures.Settings.Llm, Fixtures.Log, coordinator);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = Fixtures.Settings.Llm.Model,
            ["messages"] = Array.Empty<JsonElement>(),
            ["max_completion_tokens"] = 1024
        };

        var error = await Assert.ThrowsAsync<LlmException>(() => transport.SendAsync(payload, default));

        Assert.Equal("rate_limit", error.Code);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task CancellationStopsKnownQuotaWait()
    {
        var coordinator = new LlmRequestCoordinator(new Clock(), (_, ct) => Task.FromCanceled(ct));
        var window = coordinator.For("cancel");
        window.BlockedUntil = new Clock().Now.AddSeconds(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.WaitAsync(window, 1, TimeSpan.FromSeconds(60), cancellation.Token));
    }
}
