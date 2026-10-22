
namespace IntellectBot.Domain;

public sealed record LlmResponse(string Text, int? PromptTokens = null, int? CompletionTokens = null, string? Mode = null)
{
    public IReadOnlyList<PreparedReminder> PendingActions { get; init; } = Array.Empty<PreparedReminder>();
    public bool IncludeTextWithPendingActions
    {
        get; init;
    }
    public override string ToString() => "LlmResponse([hidden])";
}
