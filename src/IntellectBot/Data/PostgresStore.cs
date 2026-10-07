using IntellectBot.Domain;
using Npgsql;

namespace IntellectBot.Data;

public sealed class PostgresStore(NpgsqlDataSource dataSource) : IDialogueStore
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS bot_users (
            chat_id BIGINT PRIMARY KEY,
            mode TEXT NOT NULL DEFAULT 'study' CHECK (mode IN ('study', 'translate', 'review')),
            temperature DOUBLE PRECISION NOT NULL DEFAULT 0.3 CHECK (temperature IN (0.0, 0.3, 0.7, 1.0)),
            created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE TABLE IF NOT EXISTS dialogue_messages (
            id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            chat_id BIGINT NOT NULL REFERENCES bot_users(chat_id) ON DELETE CASCADE,
            role TEXT NOT NULL CHECK (role IN ('user', 'assistant')),
            content TEXT NOT NULL CHECK (content <> ''),
            created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE INDEX IF NOT EXISTS dialogue_messages_chat_id_id_idx ON dialogue_messages(chat_id, id);
        """;
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand(Schema, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        command.CommandTimeout = 3;
        return (int)(await command.ExecuteScalarAsync(ct))! == 1;
    }
    public async Task<UserSettings> GetOrCreateAsync(long chatId, CancellationToken ct)
    {
        await using (var insert = dataSource.CreateCommand("INSERT INTO bot_users(chat_id) VALUES ($1) ON CONFLICT DO NOTHING"))
        {
            insert.Parameters.AddWithValue(chatId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using var command = dataSource.CreateCommand("SELECT mode, temperature FROM bot_users WHERE chat_id=$1");
        command.Parameters.AddWithValue(chatId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new IOException("User settings unavailable.");
        return new(reader.GetString(0), reader.GetDouble(1));
    }
    public async Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(long chatId, int maxMessages, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT role, content FROM (SELECT id, role, content FROM dialogue_messages WHERE chat_id=$1 ORDER BY id DESC LIMIT $2) AS recent ORDER BY id ASC");
        command.Parameters.AddWithValue(chatId);
        command.Parameters.AddWithValue(Math.Max(0, maxMessages / 2 * 2));
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<ChatMessage> messages = [];
        while (await reader.ReadAsync(ct))
            messages.Add(new(reader.GetString(0), reader.GetString(1)));
        return messages;
    }
    public async Task SaveTurnAsync(long chatId, string question, string answer, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var message in new[] { new ChatMessage("user", question), new("assistant", answer) })
        {
            await using var command = new NpgsqlCommand("INSERT INTO dialogue_messages(chat_id, role, content) VALUES ($1,$2,$3)", connection, transaction);
            command.Parameters.AddWithValue(chatId);
            command.Parameters.AddWithValue(message.Role);
            command.Parameters.AddWithValue(message.Content);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
    public async Task ClearHistoryAsync(long chatId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM dialogue_messages WHERE chat_id=$1");
        command.Parameters.AddWithValue(chatId);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task SetModeAsync(long chatId, string mode, CancellationToken ct)
    {
        _ = Modes.Name(mode);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand("INSERT INTO bot_users(chat_id,mode) VALUES ($1,$2) ON CONFLICT(chat_id) DO UPDATE SET mode=EXCLUDED.mode", connection, transaction))
        {
            command.Parameters.AddWithValue(chatId);
            command.Parameters.AddWithValue(mode);
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var command = new NpgsqlCommand("DELETE FROM dialogue_messages WHERE chat_id=$1", connection, transaction))
        {
            command.Parameters.AddWithValue(chatId);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
    public async Task SetTemperatureAsync(long chatId, double temperature, CancellationToken ct)
    {
        Modes.ValidateTemperature(temperature);
        await using var command = dataSource.CreateCommand("INSERT INTO bot_users(chat_id,temperature) VALUES ($1,$2) ON CONFLICT(chat_id) DO UPDATE SET temperature=EXCLUDED.temperature");
        command.Parameters.AddWithValue(chatId);
        command.Parameters.AddWithValue(temperature);
        await command.ExecuteNonQueryAsync(ct);
    }
}
