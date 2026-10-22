using IntellectBot.Agent;
using IntellectBot.Domain;
using IntellectBot.Mcp;

namespace IntellectBot.Tests;

internal sealed class UiClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class MemoryReminderLifecycle(UiClock clock) : IReminderLifecycle
{
    public long Owner { get; set; } = 42;
    public PreparedReminder Action
    {
        get; set;
    } = new(Guid.NewGuid(),
        ReminderValidation.Parse("Проверить код", "2026-10-12T18:00:00+03:00", "Europe/Moscow"),
        "pending", clock.Now, clock.Now.AddMinutes(5), null);
    public int Confirmations
    {
        get; private set;
    }
    public int Cancellations
    {
        get; private set;
    }
    public Task<PreparedReminder?> GetAsync(long owner, Guid id, CancellationToken ct) =>
        Task.FromResult(owner == Owner && id == Action.Id ? Action : null);
    public Task<PreparedReminder> ConfirmAsync(long owner, Guid id, CancellationToken ct)
    {
        Confirmations++;
        if (owner != Owner || id != Action.Id)
            throw new ReminderException("not_found", "Действие не найдено.");
        if (Action.Status == "executed")
            return Task.FromResult(Action);
        if (Action.ExpiresAt <= clock.Now || Action.Draft.RemindAt <= clock.Now)
        {
            Action = Action with
            {
                Status = "expired"
            };
            throw new ReminderException("expired", "Срок подтверждения истёк.");
        }
        Action = Action with
        {
            Status = "confirmed",
            ConfirmedAt = clock.Now
        };
        return Task.FromResult(Action);
    }
    public Task CancelAsync(long owner, Guid id, CancellationToken ct)
    {
        Cancellations++;
        Action = Action with
        {
            Status = "cancelled",
            ConfirmedAt = null
        };
        return Task.CompletedTask;
    }
}

internal sealed class ConfirmedGateway(MemoryReminderLifecycle lifecycle) : IConfirmedReminderGateway
{
    public McpToolCatalog Catalog { get; init; } = new(AgentTestSupport.Tools);
    public int Writes
    {
        get; private set;
    }
    public bool Fail
    {
        get; set;
    }
    public bool LoseResponse
    {
        get; set;
    }
    public Task<McpToolOutcome> CallReadOnlyAsync(string name, System.Text.Json.JsonElement args, CancellationToken cancellationToken = default) =>
        Task.FromResult(new McpToolOutcome(false, null, "not_used"));
    public Task<McpToolOutcome> ExecuteConfirmedReminderAsync(long owner, PreparedReminder action, CancellationToken cancellationToken = default)
    {
        Writes++;
        if (Fail)
            return Task.FromResult(new McpToolOutcome(false, null, "tool_unavailable"));
        if (action.Status != "confirmed" || owner != lifecycle.Owner)
            throw new InvalidOperationException();
        lifecycle.Action = action with
        {
            Status = "executed"
        };
        if (LoseResponse)
            return Task.FromResult(new McpToolOutcome(false, null, "tool_unavailable"));
        return Task.FromResult(new McpToolOutcome(true, AgentTestSupport.Json(new
        {
            id = Guid.NewGuid(),
            remind_at = ReminderAuthorization.FormatTime(action.Draft.RemindAt),
            timezone = action.Draft.TimeZone,
            created = true
        }), null));
    }
}
