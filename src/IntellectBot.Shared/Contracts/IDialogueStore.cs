namespace IntellectBot.Domain;

public interface IDialogueStore
{
    Task<UserSettings> GetOrCreateAsync(long chatId, CancellationToken ct);
    Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(long chatId, int maxMessages, CancellationToken ct);
    Task SaveTurnAsync(long chatId, string question, string answer, CancellationToken ct);
    Task ClearHistoryAsync(long chatId, CancellationToken ct);
    Task SetModeAsync(long chatId, string mode, CancellationToken ct);
    Task SetTemperatureAsync(long chatId, double temperature, CancellationToken ct);
    Task SetModelAsync(long chatId, string model, CancellationToken ct);
}
