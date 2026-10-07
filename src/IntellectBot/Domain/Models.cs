using System.Text.Json.Serialization;

namespace IntellectBot.Domain;

public sealed record ChatMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content)
{
    public override string ToString() => $"ChatMessage({Role}, [hidden])";
}
public sealed record UserSettings(string Mode = "study", double Temperature = 0.3);
public sealed record DialogueSettings(string Mode, double Temperature, string Model);
public sealed record LlmResponse(string Text, int? PromptTokens = null, int? CompletionTokens = null, string? Mode = null)
{
    public override string ToString() => "LlmResponse([hidden])";
}
public sealed class DialogueException(string message) : Exception(message);
public static class Modes
{
    public static readonly string[] Ordered = ["study", "review", "translate"];
    public static readonly double[] Temperatures = [0.0, 0.3, 0.7, 1.0];
    public const string InvalidTemperature = "Допустимые значения уровня креативности: 0.0, 0.3, 0.7, 1.0.";
    public static string Name(string mode) => mode switch { "study" => "Обучение", "review" => "Проверка кода", "translate" => "Перевод", _ => throw new DialogueException("Неизвестный режим. Выберите режим в настройках.") };
    public static void ValidateTemperature(double value)
    {
        if (!Temperatures.Contains(value))
            throw new DialogueException(InvalidTemperature);
    }
}
public interface ILlmClient
{
    Task<LlmResponse> GenerateAsync(IReadOnlyList<ChatMessage> messages, double temperature, CancellationToken cancellationToken);
}
public interface IDialogueStore
{
    Task<UserSettings> GetOrCreateAsync(long chatId, CancellationToken ct);
    Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(long chatId, int maxMessages, CancellationToken ct);
    Task SaveTurnAsync(long chatId, string question, string answer, CancellationToken ct);
    Task ClearHistoryAsync(long chatId, CancellationToken ct);
    Task SetModeAsync(long chatId, string mode, CancellationToken ct);
    Task SetTemperatureAsync(long chatId, double temperature, CancellationToken ct);
}
public interface IDialogueService
{
    Task<LlmResponse> ReplyAsync(long chatId, string text, CancellationToken ct);
    Task ResetAsync(long chatId, CancellationToken ct);
    Task SetModeAsync(long chatId, string mode, CancellationToken ct);
    Task SetTemperatureAsync(long chatId, double temperature, CancellationToken ct);
    Task<DialogueSettings> GetSettingsAsync(long chatId, CancellationToken ct);
}
