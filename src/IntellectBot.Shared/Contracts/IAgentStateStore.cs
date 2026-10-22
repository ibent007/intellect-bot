namespace IntellectBot.Domain;

public interface IAgentStateStore
{
    Task<string?> GetTimeZoneAsync(
        long chatId, CancellationToken ct);

    Task SetTimeZoneAsync(
        long chatId, string timeZone, CancellationToken ct);

    Task SaveAuditAsync(
        long chatId, AgentAuditEntry audit, CancellationToken ct);

    Task<AgentAuditEntry?> GetLastAuditAsync(
        long chatId, CancellationToken ct);
}
