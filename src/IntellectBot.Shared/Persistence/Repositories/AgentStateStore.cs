using System.Text.Json.Serialization;
using System.Text.Json;
using IntellectBot.Domain;
using Npgsql;
using NpgsqlTypes;

namespace IntellectBot.Data;

public sealed class AgentStateStore(NpgsqlDataSource dataSource)
    : IAgentStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public async Task<string?> GetTimeZoneAsync(
        long chatId,
        CancellationToken ct)
    {
        AgentStateValidation.ValidateOwner(chatId);

        await using var command = dataSource.CreateCommand(
            "SELECT time_zone FROM bot_users WHERE chat_id=$1");

        command.Parameters.AddWithValue(chatId);

        var value = await command.ExecuteScalarAsync(ct);

        return value is string name
            ? AgentStateValidation.ValidateTimeZone(name)
            : null;
    }

    public async Task SetTimeZoneAsync(
        long chatId,
        string timeZone,
        CancellationToken ct)
    {
        AgentStateValidation.ValidateOwner(chatId);
        var name = AgentStateValidation.ValidateTimeZone(timeZone);

        await using var command = dataSource.CreateCommand("""
            INSERT INTO bot_users(chat_id, time_zone)
            VALUES ($1, $2)
            ON CONFLICT(chat_id)
            DO UPDATE SET time_zone=EXCLUDED.time_zone
            """);

        command.Parameters.AddWithValue(chatId);
        command.Parameters.AddWithValue(name);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAuditAsync(
        long chatId,
        AgentAuditEntry audit,
        CancellationToken ct)
    {
        AgentStateValidation.ValidateOwner(chatId);
        AgentStateValidation.ValidateAudit(audit);

        var toolsJson = JsonSerializer.Serialize(audit.Tools, JsonOptions);

        await using var connection =
            await dataSource.OpenConnectionAsync(ct);

        await using var transaction =
            await connection.BeginTransactionAsync(ct);

        await using (var owner = new NpgsqlCommand("""
            INSERT INTO bot_users(chat_id)
            VALUES ($1)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            owner.Parameters.AddWithValue(chatId);
            await owner.ExecuteNonQueryAsync(ct);
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO agent_audits(
                owner_chat_id, request_id, created_at,
                action, tools, reason)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT(owner_chat_id, request_id)
            DO UPDATE SET
                action=EXCLUDED.action,
                tools=EXCLUDED.tools,
                reason=EXCLUDED.reason
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(chatId);
            command.Parameters.AddWithValue(audit.RequestId);
            command.Parameters.AddWithValue(
                audit.CreatedAt.ToUniversalTime());
            command.Parameters.AddWithValue(audit.Action);
            command.Parameters.AddWithValue(
                NpgsqlDbType.Jsonb, toolsJson);
            command.Parameters.AddWithValue(audit.Reason);

            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<AgentAuditEntry?> GetLastAuditAsync(
        long chatId,
        CancellationToken ct)
    {
        AgentStateValidation.ValidateOwner(chatId);

        await using var command = dataSource.CreateCommand("""
            SELECT request_id, created_at, action, tools, reason
            FROM agent_audits
            WHERE owner_chat_id=$1 AND action IN ('respond', 'clarify', 'call_tool')
            ORDER BY created_at DESC, id DESC
            LIMIT 1
            """);

        command.Parameters.AddWithValue(chatId);

        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return null;

        var tools = JsonSerializer.Deserialize<ToolAuditEntry[]>(
            reader.GetString(3), JsonOptions)
            ?? throw new InvalidDataException(
                "Некорректный сохранённый аудит.");

        var audit = new AgentAuditEntry(
            reader.GetGuid(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetString(2),
            tools,
            reader.GetString(4));

        AgentStateValidation.ValidateAudit(audit);

        return audit;
    }
}
