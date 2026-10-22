using IntellectBot.Domain;
using Npgsql;

namespace IntellectBot.Data;

// Хранит только настройки и историю диалога. Создание схемы выполняет DatabaseMigrator.
public sealed class DialogueStore(NpgsqlDataSource dataSource) : IDialogueStore
{
    public async Task<UserSettings> GetOrCreateAsync(long chatId, CancellationToken ct)
    {
        await using (var insert = dataSource.CreateCommand("INSERT INTO bot_users(chat_id) VALUES ($1) ON CONFLICT DO NOTHING"))
        {
            insert.Parameters.AddWithValue(chatId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using var command = dataSource.CreateCommand("SELECT mode, temperature, model_id FROM bot_users WHERE chat_id=$1");
        command.Parameters.AddWithValue(chatId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new IOException("User settings unavailable.");
        return new(reader.GetString(0), reader.GetDouble(1), reader.IsDBNull(2) ? null : reader.GetString(2));
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
    public async Task SetModelAsync(long chatId, string model, CancellationToken ct)
    {
        if (!LlmModels.IsAllowed(model))
            throw new ArgumentException(
                "Выберите модель из списка в настройках.");

        await using var command = dataSource.CreateCommand(
            "INSERT INTO bot_users(chat_id,model_id) VALUES ($1,$2) " +
            "ON CONFLICT(chat_id) DO UPDATE SET model_id=EXCLUDED.model_id");

        command.Parameters.AddWithValue(chatId);
        command.Parameters.AddWithValue(model);
        await command.ExecuteNonQueryAsync(ct);
    }
}
