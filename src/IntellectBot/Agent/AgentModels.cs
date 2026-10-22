using System.Text.Json;
using IntellectBot.Domain;
using IntellectBot.Mcp;

namespace IntellectBot.Agent;

public sealed record AgentToolCall(string Id, string Name, string Arguments)
{
    public override string ToString() => "AgentToolCall([hidden])";
}

public sealed record AgentChoice(int Id, string Label);

public sealed record AgentModelTurn(JsonElement Message, string? Text, IReadOnlyList<AgentToolCall> Calls)
{
    public string Decision { get; init; } = "respond";
    public IReadOnlyList<AgentChoice> Choices { get; init; } = Array.Empty<AgentChoice>();
    public override string ToString() => "AgentModelTurn([hidden])";
}

public interface IAgentLlmClient
{
    Task<AgentModelTurn> CompleteAsync(
        IReadOnlyList<JsonElement> messages,
        IReadOnlyList<McpToolDefinition> tools,
        double temperature,
        bool allowTools,
        CancellationToken ct,
        string? model = null);
}

public interface IUpdateDialogueService
{
    Task<LlmResponse> ReplyToUpdateAsync(long chatId, long updateId, string text, CancellationToken ct);
}
