
namespace IntellectBot.Domain;

public sealed record AgentAuditEntry(
    Guid RequestId,
    DateTimeOffset CreatedAt,
    string Action,
    ToolAuditEntry[] Tools,
    string Reason)
{
    public override string ToString() => "AgentAuditEntry([hidden])";
}

