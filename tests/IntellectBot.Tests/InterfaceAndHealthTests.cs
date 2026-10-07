using System.Text.Json;
using IntellectBot.Domain;
using IntellectBot.Hosting;
using IntellectBot.Infrastructure;
using IntellectBot.Telegram;
using Xunit;

namespace IntellectBot.Tests;

public sealed class InterfaceAndHealthTests
{
    [Fact]
    public void UiMatchesExpectedText()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected-ui.json")));
        var root = fixture.RootElement;
        var actualStartText = MenuView.StartText;
        Assert.Equal(root.GetProperty("start").GetString(), actualStartText);
        var actualUnknownText = MenuView.UnknownText;
        Assert.Equal(root.GetProperty("unknown").GetString(), actualUnknownText);
        var actualEmptyText = MenuView.EmptyText;
        Assert.Equal(root.GetProperty("empty").GetString(), actualEmptyText);
        foreach (var row in root.GetProperty("settings").EnumerateArray())
            Assert.Equal(row.GetProperty("text").GetString(), MenuView.SettingsText(new(row.GetProperty("mode").GetString()!, row.GetProperty("temperature").GetDouble(), row.GetProperty("model").GetString()!)));
    }
    [Theory]
    [InlineData(true, true, true, 200)]
    [InlineData(false, true, true, 503)]
    [InlineData(true, false, true, 503)]
    [InlineData(true, true, false, 503)]
    public async Task ReadinessRequiresAllThreeConditions(bool initialized, bool polling, bool database, int status)
    {
        var result = await HealthProbe.CheckAsync(new()
        {
            Initialized = initialized,
            Polling = polling
        }, _ => Task.FromResult(database), default);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(database ? "ok" : "unavailable", result.Database);
        Assert.Equal(polling ? "running" : "stopped", result.Polling);
    }
    [Fact]
    public async Task HealthDoesNotExposeDatabaseExceptions()
    {
        var result = await HealthProbe.CheckAsync(new()
        {
            Initialized = true,
            Polling = true
        }, _ => throw new IOException("password=secret"), default);
        Assert.Equal(503, result.StatusCode);
        Assert.DoesNotContain("secret", result.ToString());
    }
    [Fact]
    public async Task CancelledLockWaiterDoesNotLeakOrBlockNextRequest()
    {
        var locks = new ChatLocks();
        var first = await locks.AcquireAsync(42, default);
        using var cancellation = new CancellationTokenSource();
        var waiting = locks.AcquireAsync(42, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        using var next = await locks.AcquireAsync(42, default);
    }
    [Fact]
    public async Task DifferentChatsProceedIndependently()
    {
        var locks = new ChatLocks();
        using var first = await locks.AcquireAsync(1, default);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var second = await locks.AcquireAsync(2, cancellation.Token);
        Assert.False(cancellation.IsCancellationRequested);
    }
    [Fact]
    public async Task TypingContinuesWhileModelIsWorking()
    {
        var api = new FakeTelegram();
        var llm = new FakeLlm { BeforeResponse = ct => Task.Delay(TimeSpan.FromSeconds(4.3), ct) };
        var handler = new UpdateHandler(api, new DialogueService(new MemoryStore(), llm, Fixtures.Settings.Llm), Fixtures.Log);
        await handler.HandleAsync(Fixtures.Message("question"), default);
        Assert.True(api.TypingCount >= 2);
        Assert.Single(api.Messages);
    }
}
