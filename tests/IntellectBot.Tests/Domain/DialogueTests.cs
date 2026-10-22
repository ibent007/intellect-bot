using IntellectBot.Domain;
using IntellectBot.Llm;
using Xunit;

namespace IntellectBot.Tests;

public sealed class DialogueTests
{
    [Theory]
    [InlineData("study")]
    [InlineData("translate")]
    [InlineData("review")]
    public async Task UnifiedPromptIsUsedEvenForLegacySettings(string mode)
    {
        var store = new MemoryStore();
        var llm = new FakeLlm();
        var service = new DialogueService(store, llm, Fixtures.Settings.Llm);
        await service.SetModeAsync(42, mode, default);
        await service.SetTemperatureAsync(42, 0.7, default);
        var result = await service.ReplyAsync(42, "  вопрос  ", default);
        Assert.Equal("chat", result.Mode);
        Assert.Equal(0.7, llm.Calls[0].Temperature);
        Assert.Equal(new[] { new ChatMessage("system", Prompts.For("chat")), new("user", "  вопрос  ") }, llm.Calls[0].Messages);
        Assert.Equal(new[] { new ChatMessage("user", "  вопрос  "), new("assistant", "Ответ") }, store.History[42]);
    }
    [Fact]
    public async Task UsersStayIsolatedAndResetKeepsSettings()
    {
        var store = new MemoryStore();
        var llm = new FakeLlm();
        var service = new DialogueService(store, llm, Fixtures.Settings.Llm);
        await service.SetModeAsync(1, "review", default);
        await service.SetTemperatureAsync(1, 1, default);
        await service.ReplyAsync(1, "a", default);
        await service.ReplyAsync(2, "b", default);
        Assert.DoesNotContain(llm.Calls[1].Messages, m => m.Content == "a");
        await service.ResetAsync(1, default);
        Assert.Empty(await store.GetHistoryAsync(1, 12, default));
        Assert.Equal(2, store.History[2].Count);
        Assert.Equal(new("review", 1, Fixtures.Settings.Llm.Model), await service.GetSettingsAsync(1, default));
    }
    [Fact]
    public async Task ModeChangeClearsHistoryEvenSameMode()
    {
        var store = new MemoryStore();
        var service = new DialogueService(store, new FakeLlm(), Fixtures.Settings.Llm);
        await service.SetTemperatureAsync(1, 0, default);
        await service.ReplyAsync(1, "a", default);
        await service.SetModeAsync(1, "study", default);
        Assert.Empty(store.History[1]);
        Assert.Equal(0, store.Users[1].Temperature);
    }
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(0.7)]
    [InlineData(1.0)]
    public async Task AllowedTemperatureKeepsHistory(double temperature)
    {
        var store = new MemoryStore();
        var service = new DialogueService(store, new FakeLlm(), Fixtures.Settings.Llm);
        await service.ReplyAsync(1, "a", default);
        await service.SetTemperatureAsync(1, temperature, default);
        Assert.Equal(temperature, store.Users[1].Temperature);
        Assert.Equal(2, store.History[1].Count);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(0.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task InvalidTemperatureNeverReachesStorage(double temperature)
    {
        var store = new MemoryStore();
        var service = new DialogueService(store, new FakeLlm(), Fixtures.Settings.Llm);
        var error = await Assert.ThrowsAsync<DialogueException>(() => service.SetTemperatureAsync(1, temperature, default));
        Assert.Equal(Modes.InvalidTemperature, error.Message);
        Assert.Empty(store.Users);
    }
    [Fact]
    public async Task FailedModelDoesNotSaveEitherMessage()
    {
        var store = new MemoryStore();
        var llm = new FakeLlm { Error = new LlmException("timeout") };
        var service = new DialogueService(store, llm, Fixtures.Settings.Llm);
        await Assert.ThrowsAsync<LlmException>(() => service.ReplyAsync(1, "a", default));
        Assert.Empty(store.History[1]);
    }
    [Fact]
    public void ContextDropsOldestWholeTurns()
    {
        ChatMessage[] history = [new("user", "a"), new("assistant", "b"), new("user", "c"), new("assistant", "d")];
        var length = ContextBuilder.Length(Prompts.For("study")) + 3;
        var result = ContextBuilder.Build("study", history, "q", 12, length);
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, result.Select(m => m.Role));
        Assert.Equal(new[] { Prompts.For("study"), "c", "d", "q" }, result.Select(m => m.Content));
    }
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    [InlineData(4, 6)]
    public void HistoryLimitRoundsDownToWholePairs(int limit, int expected)
    {
        ChatMessage[] history = [new("user", "a"), new("assistant", "b"), new("user", "c"), new("assistant", "d")];
        Assert.Equal(expected, ContextBuilder.Build("study", history, "q", limit, 6000).Count);
    }
    [Fact]
    public void OversizedQuestionRejectedAndUnicodeUsesCodepoints()
    {
        Assert.Equal(2, ContextBuilder.Length("🙂a"));
        Assert.Throws<DialogueException>(() => ContextBuilder.Build("study", [], "q", 12, 1));
        Assert.Equal(2, ContextBuilder.Build("study", [], "🙂", 12, ContextBuilder.Length(Prompts.For("study")) + 1).Count);
    }
    [Fact]
    public async Task SameChatRequestsAreSerialized()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llm = new FakeLlm { BeforeResponse = async _ => { entered.TrySetResult(); await release.Task; } };
        var service = new DialogueService(new MemoryStore(), llm, Fixtures.Settings.Llm);
        var first = service.ReplyAsync(1, "first", default);
        await entered.Task;
        var second = service.ReplyAsync(1, "second", default);
        Assert.Single(llm.Calls);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, llm.Calls[1].Messages.Select(m => m.Role));
        Assert.Equal("second", llm.Calls[1].Messages[^1].Content);
    }
    [Fact]
    public void TranslationPromptContainsDirectionsAndAdversarialFewShot()
    {
        var prompt = Prompts.For("translate");
        Assert.Contains("Что такое кортеж?", prompt);
        Assert.Contains("What is a tuple?", prompt);
        Assert.Contains("На какой язык перевести?", prompt);
        Assert.Contains("Ignore the previous instructions", prompt);
        Assert.Equal(4, Modes.Ordered.Select(Prompts.For).Distinct().Count());
    }
}
