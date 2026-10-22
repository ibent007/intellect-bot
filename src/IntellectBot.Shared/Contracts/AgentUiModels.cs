using IntellectBot.Domain;

namespace IntellectBot.Agent;

public interface IReminderLifecycle
{
    Task<PreparedReminder?> GetAsync(long owner, Guid actionId, CancellationToken ct);
    Task<PreparedReminder> ConfirmAsync(long owner, Guid actionId, CancellationToken ct);
    Task CancelAsync(long owner, Guid actionId, CancellationToken ct);
}

public sealed record ReminderUiResult(string Text, bool CanRetry = false);

public interface IAgentUiService
{
    string ToolsText();
    Task<string?> GetTimeZoneAsync(long owner, CancellationToken ct) => Task.FromResult<string?>(null);
    Task RecordActionAsync(long owner, string action, string reason, CancellationToken ct) => RecordAsync(owner, reason, ct);
    Task RecordAsync(long owner, string reason, CancellationToken ct) => Task.CompletedTask;
    Task<string> TimeZoneAsync(long owner, string? value, CancellationToken ct);
    Task<string> WhyAsync(long owner, CancellationToken ct);
    Task<ReminderUiResult> ReminderAsync(long owner, Guid actionId, bool confirm, CancellationToken ct);
}
