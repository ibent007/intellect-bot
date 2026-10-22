namespace IntellectBot.Domain;

public interface IDialogueService
{
    Task<LlmResponse> ReplyAsync(long chatId, string text, CancellationToken ct);
    Task ResetAsync(long chatId, CancellationToken ct);
    Task SetModeAsync(long chatId, string mode, CancellationToken ct);
    Task SetTemperatureAsync(long chatId, double temperature, CancellationToken ct);
    Task<DialogueSettings> GetSettingsAsync(long chatId, CancellationToken ct);
    Task SetModelAsync(long chatId, string model, CancellationToken ct);
}
