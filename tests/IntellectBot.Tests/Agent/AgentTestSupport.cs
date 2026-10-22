using System.Text.Json;
using IntellectBot.Agent;
using IntellectBot.Domain;
using IntellectBot.Mcp;

namespace IntellectBot.Tests;

internal static class AgentTestSupport
{
    public static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    public static McpToolDefinition Tool(string name, params string[] fields) => new(name, "Описание",
        Json(new
        {
            type = "object",
            properties = (name == "get_weather" ? fields.Append("locationId") : fields).ToDictionary(field => field, field => Json(
                name == "get_schedule" && field == "date" ? (object)new
                {
                    type = "string",
                    format = "date"
                } :
                field == "city" ? new
                {
                    type = "string",
                    minLength = 1,
                    maxLength = 100,
                    pattern = @"\S"
                } :
                new
                {
                    type = "string"
                })),
            additionalProperties = false,
            required = fields
        }),
        Json(new
        {
            type = "object"
        }), name != "add_reminder");
    public static IReadOnlyList<McpToolDefinition> Tools => new[]
    { Tool("get_weather", "city"), Tool("get_schedule", "date"), Tool("add_reminder", "text", "remind_at") };
    public static AgentToolCall Call(string id, string name, object args) => new(id, name, JsonSerializer.Serialize(args));
    public static AgentModelTurn Calls(params AgentToolCall[] calls) => new(Json(new
    {
        role = "assistant",
        content = (string?)null,
        tool_calls = calls.Select(call => new { id = call.Id, type = "function", function = new { name = call.Name, arguments = call.Arguments } })
    }), null, calls);
    public static AgentModelTurn Answer(string text = "Готово") => new(Json(new { role = "assistant", content = text }), text, Array.Empty<AgentToolCall>());
}

internal sealed class AgentClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
}

internal sealed class ScriptedAgentLlm(params AgentModelTurn[] turns) : IAgentLlmClient
{
    public List<(IReadOnlyList<JsonElement> Messages, bool AllowTools)> Requests { get; } = [];
    public Task<AgentModelTurn> CompleteAsync(IReadOnlyList<JsonElement> messages,
        IReadOnlyList<McpToolDefinition> tools, double temperature, bool allowTools, CancellationToken ct, string? model = null)
    {
        Requests.Add((messages.ToArray(), allowTools));
        return Task.FromResult(turns[Requests.Count - 1]);
    }
}

internal sealed class MemoryAgentState : IAgentStateStore
{
    public string? Zone { get; set; } = "Europe/Moscow";
    public AgentAuditEntry? Last
    {
        get; private set;
    }
    public long Owner
    {
        get; private set;
    }
    public Task<string?> GetTimeZoneAsync(long chatId, CancellationToken ct) => Task.FromResult(Zone);
    public Task SetTimeZoneAsync(long chatId, string timeZone, CancellationToken ct)
    {
        Zone = timeZone;
        return Task.CompletedTask;
    }
    public Task SaveAuditAsync(long chatId, AgentAuditEntry audit, CancellationToken ct)
    {
        AgentStateValidation.ValidateAudit(audit);
        Owner = chatId;
        Last = audit;
        return Task.CompletedTask;
    }
    public Task<AgentAuditEntry?> GetLastAuditAsync(long chatId, CancellationToken ct) => Task.FromResult(Last);
}

internal sealed class RecordingGateway : IMcpGateway
{
    public McpToolCatalog Catalog { get; init; } = new(AgentTestSupport.Tools);
    public List<(string Name, JsonElement Args)> Calls { get; } = [];
    public McpToolOutcome Outcome
    {
        get; set;
    } = new(true,
        AgentTestSupport.Json(new
        {
            date = "2026-10-12",
            lessons = Array.Empty<object>()
        }), null);
    public Task<McpToolOutcome> CallReadOnlyAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add((toolName, arguments));
        return Task.FromResult(Outcome);
    }
}

internal sealed class MemoryReminderPreparation : IReminderPreparation
{
    public List<(long Owner, long Update, int Index, string Text)> Calls { get; } = [];
    public Task<PreparedReminder> PrepareAsync(long owner, long updateId, int proposalIndex, string text, string remindAt, CancellationToken ct)
    {
        Calls.Add((owner, updateId, proposalIndex, text));
        var now = new AgentClock().GetUtcNow();
        return Task.FromResult(new PreparedReminder(Guid.NewGuid(),
            ReminderValidation.Parse(text, remindAt, "Europe/Moscow"), "pending", now, now.AddMinutes(5), null));
    }
}
