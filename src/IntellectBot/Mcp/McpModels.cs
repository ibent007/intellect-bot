using System.Text.Json;

namespace IntellectBot.Mcp;

public sealed record McpToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema,
    JsonElement OutputSchema,
    bool ReadOnly);

public sealed record McpToolOutcome(
    bool Success,
    JsonElement? Data,
    string? ErrorCode)
{
    public override string ToString() => nameof(McpToolOutcome);
}

public sealed class McpUnavailableException()
    : Exception("MCP-сервер сейчас недоступен.");

public interface IMcpGateway
{
    McpToolCatalog Catalog
    {
        get;
    }
    Task<McpToolOutcome> CallReadOnlyAsync(string toolName, JsonElement arguments,
        CancellationToken cancellationToken = default);
}

public interface IConfirmedReminderGateway : IMcpGateway
{
    Task<McpToolOutcome> ExecuteConfirmedReminderAsync(long ownerChatId,
        IntellectBot.Domain.PreparedReminder action, CancellationToken cancellationToken = default);
}

// Чтение от имени владельца, определённого Telegram-приложением
public interface IOwnerMcpGateway : IMcpGateway
{
    Task<McpToolOutcome> CallForOwnerAsync(long owner, string toolName, JsonElement arguments, CancellationToken ct);
}
