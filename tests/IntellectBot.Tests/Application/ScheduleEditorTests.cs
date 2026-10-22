using IntellectBot.Application;
using IntellectBot.Domain;
using IntellectBot.Mcp;
using IntellectBot.Telegram;
using Xunit;
namespace IntellectBot.Tests;

public sealed class ScheduleEditorTests
{
    private const string Line = "2030-01-15 | 09:00 | 10:30 | Программирование | 101";
    [Theory]
    [InlineData("menu")]
    [InlineData("settings")]
    public async Task ScheduleReturnsToSettingsAndCanCancelEveryInput(string prefix)
    {
        var store = new ScheduleMemory();
        var controller = new WorkspaceController(new ScheduleEditor(store, new MemoryAgentState(), new AgentClock()));
        var menu = await controller.OpenAsync(42, prefix + ":home", default);
        Assert.Equal(prefix + ":settings", menu.Keyboard.Rows.Last()[0].CallbackData);
        foreach (var action in new[] { "add", "import", "delete" })
        {
            var input = await controller.OpenAsync(42, prefix + ":" + action, default);
            var cancel = input.Keyboard.Rows[0][0].CallbackData;
            Assert.Equal("workspace:" + prefix + ":home", cancel);
            await controller.OpenAsync(42, cancel[10..], default);
            Assert.False(controller.AwaitingInput(42));
        }
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task CancelledPreviewButtonCannotSaveLater()
    {
        var store = new ScheduleMemory();
        var controller = new WorkspaceController(new ScheduleEditor(store, new MemoryAgentState(), new AgentClock()));
        await controller.OpenAsync(42, "settings:import", default);
        var preview = controller.Input(42, Line);
        var save = preview.Keyboard.Rows[0][0].CallbackData;
        await controller.OpenAsync(42, "settings:home", default);
        var result = await controller.OpenAsync(42, save[10..], default);
        Assert.Contains("истекло", result.Text);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task PreviewDoesNotWriteAndConfirmationIsOwnedAndSingleUse()
    {
        var store = new ScheduleMemory();
        var state = new MemoryAgentState();
        var editor = new ScheduleEditor(store, state, new AgentClock());
        editor.Begin(42, false);
        var draft = editor.Preview(42, Line);
        Assert.Equal(0, store.Writes);
        Assert.False(await editor.ConfirmAsync(99, draft.Id, default));
        Assert.True(await editor.ConfirmAsync(42, draft.Id, default));
        Assert.False(await editor.ConfirmAsync(42, draft.Id, default));
        Assert.Equal(1, store.Writes);
        Assert.Equal(42, store.Owner);
        Assert.Contains("обновление", state.Last!.Reason);
    }
    [Fact]
    public async Task CancellationAndExpiredButtonsNeverWrite()
    {
        var clock = new MutableClock();
        var store = new ScheduleMemory();
        var editor = new ScheduleEditor(store, new MemoryAgentState(), clock);
        editor.Begin(42, false);
        var draft = editor.Preview(42, Line);
        editor.Cancel(42);
        Assert.False(await editor.ConfirmAsync(42, draft.Id, default));
        editor.Begin(42, false);
        draft = editor.Preview(42, Line);
        clock.Now = clock.Now.AddMinutes(6);
        Assert.False(await editor.ConfirmAsync(42, draft.Id, default));
        Assert.Equal(0, store.Writes);
    }
    [Fact]
    public async Task DeleteRequiresExactDateAndConfirmation()
    {
        var store = new ScheduleMemory();
        var editor = new ScheduleEditor(store, new MemoryAgentState(), new AgentClock());
        editor.Begin(42, true);
        Assert.Throws<ArgumentException>(() => editor.Preview(42, "завтра"));
        var draft = editor.Preview(42, "2030-01-15");
        Assert.Equal(0, store.Writes);
        await editor.ConfirmAsync(42, draft.Id, default);
        Assert.Equal(new DateOnly(2030, 1, 15), store.Deleted);
    }
    [Fact]
    public async Task WeekUsesOwnerTimeZoneAndMondayBoundary()
    {
        var clock = new MutableClock { Now = new DateTimeOffset(2026, 10, 11, 22, 30, 0, TimeSpan.Zero) };
        var store = new ScheduleMemory();
        var editor = new ScheduleEditor(store, new MemoryAgentState(), clock);
        await editor.WeekAsync(42, default);
        Assert.Equal(new DateOnly(2026, 10, 12), store.From);
        Assert.Equal(new DateOnly(2026, 10, 18), store.Through);
    }
    [Fact]
    public async Task TelegramInputIsNotSentToModelAndForeignCallbackCannotSave()
    {
        var store = new ScheduleMemory();
        var editor = new ScheduleEditor(store, new MemoryAgentState(), new AgentClock());
        var api = new FakeTelegram();
        var llm = new FakeLlm();
        var handler = new UpdateHandler(api, new DialogueService(new MemoryStore(), llm, Fixtures.Settings.Llm), Fixtures.Log,
            workspace: new WorkspaceController(editor));
        await handler.HandleAsync(Fixtures.Callback("workspace:import"), default);
        await handler.HandleAsync(Fixtures.Message(Line), default);
        Assert.Empty(llm.Calls);
        Assert.Equal(0, store.Writes);
        var button = api.Messages.Last().Keyboard!.Rows[0][0].CallbackData;
        Assert.True(button.Length <= 64);
        await handler.HandleAsync(Fixtures.Callback(button, userId: 99), default);
        Assert.Equal(0, store.Writes);
        await handler.HandleAsync(Fixtures.Callback(button), default);
        Assert.Equal(1, store.Writes);
    }
    [Fact]
    public async Task ScheduleCannotBeEditedWithoutTimeZone()
    {
        var state = new MemoryAgentState { Zone = null };
        var editor = new ScheduleEditor(new ScheduleMemory(), state, new AgentClock());
        var screen = await new WorkspaceController(editor).OpenAsync(42, "add", default);
        Assert.Contains("часовой пояс", screen.Text);
        Assert.False(editor.AwaitingInput(42));
    }
    [Fact]
    public async Task GuidedEntryAsksFiveFieldsAndAddsOnlyAfterConfirmation()
    {
        var store = new ScheduleMemory();
        var editor = new ScheduleEditor(store, new MemoryAgentState(), new AgentClock());
        editor.BeginGuided(42);
        foreach (var text in new[] { "2030-01-15", "09:00", "10:30", "Программирование" })
            Assert.NotNull(editor.Accept(42, text).Prompt);
        var result = editor.Accept(42, "-");
        Assert.True(result.Draft!.Append);
        Assert.Equal(0, store.Writes);
        await editor.ConfirmAsync(42, result.Draft.Id, default);
        Assert.Equal(1, store.Writes);
    }
    [Theory]
    [InlineData("2030-02-30 | 09:00 | 10:00 | Код | 101")]
    [InlineData("2030-01-15 | 10:00 | 09:00 | Код | 101")]
    [InlineData("2030-01-15 | 9:00 | 10:00 | Код | 101")]
    [InlineData("2030-01-15 | 09:00 | 10:00 | | 101")]
    public void InvalidInputIsRejected(string text) => Assert.Throws<ArgumentException>(() => ScheduleInput.Parse(text));
    [Fact]
    public void DuplicateInputIsRejected() => Assert.Throws<ArgumentException>(() => ScheduleInput.Parse(Line + "\n" + Line));
    [Fact]
    public void EmptyLocationIsAllowed() => Assert.Equal("", Assert.Single(ScheduleInput.Parse("2030-01-15 | 09:00 | 10:00 | Код | ")).Location);

    internal sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new AgentClock().GetUtcNow(); public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class ScheduleMemory : IPersonalScheduleStore
    {
        public int Writes; public long Owner; public DateOnly? Deleted; public DateOnly From; public DateOnly Through;
        public Task<IReadOnlyList<ScheduleEntry>> ReadAsync(long owner, DateOnly from, DateOnly through, CancellationToken ct)
        {
            Owner = owner;
            From = from;
            Through = through;
            return Task.FromResult<IReadOnlyList<ScheduleEntry>>([]);
        }
        public Task AddAsync(long owner, IReadOnlyList<ScheduleEntry> entries, CancellationToken ct) => ReplaceDaysAsync(owner, entries, ct);
        public Task ReplaceDaysAsync(long owner, IReadOnlyList<ScheduleEntry> entries, CancellationToken ct)
        {
            Writes++;
            Owner = owner;
            return Task.CompletedTask;
        }
        public Task DeleteDayAsync(long owner, DateOnly date, CancellationToken ct)
        {
            Writes++;
            Owner = owner;
            Deleted = date;
            return Task.CompletedTask;
        }
    }
}

public sealed class ToolRequestAuthorizationTests
{
    [Fact]
    public void SignedOwnerIsBoundToOperationAndExpires()
    {
        var clock = new ScheduleEditorTests.MutableClock();
        var auth = new ToolRequestAuthorization(new byte[32], clock);
        var meta = auth.Create(42, "day:2030-01-15");
        Assert.Equal(42, auth.Verify(meta, "day:2030-01-15"));
        Assert.Throws<UnauthorizedAccessException>(() => auth.Verify(meta, "day:2030-01-16"));
        Assert.Throws<UnauthorizedAccessException>(() => auth.Verify(meta, "week"));
        clock.Now = clock.Now.AddSeconds(61);
        Assert.Throws<UnauthorizedAccessException>(() => auth.Verify(meta, "day:2030-01-15"));
    }
    [Fact]
    public void MissingOrForgedContextCannotReadSchedule()
    {
        var auth = new ToolRequestAuthorization(new byte[32], new AgentClock());
        Assert.Throws<UnauthorizedAccessException>(() => auth.Verify(null, "week"));
        var forged = new ToolRequestAuthorization(Enumerable.Repeat((byte)1, 32).ToArray(), new AgentClock()).Create(42, "week");
        Assert.Throws<UnauthorizedAccessException>(() => auth.Verify(forged, "week"));
    }
}
