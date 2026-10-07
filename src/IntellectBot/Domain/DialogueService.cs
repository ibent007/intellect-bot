using IntellectBot.Configuration;
using IntellectBot.Infrastructure;
using Npgsql;

namespace IntellectBot.Domain;

public sealed class DialogueService(IDialogueStore store, ILlmClient llm, LlmSettings settings) : IDialogueService
{
    private readonly ChatLocks locks = new();
    private static bool StorageFailure(Exception error) => error is NpgsqlException or IOException or TimeoutException;
    private static async Task<T> Storage<T>(Func<Task<T>> action, string message)
    {
        try
        {
            return await action();
        }
        catch (Exception error) when (StorageFailure(error)) { throw new DialogueException(message); }
    }
    private static Task Storage(Func<Task> action, string message) => Storage(async () => { await action(); return true; }, message);
    public async Task<LlmResponse> ReplyAsync(long chatId, string text, CancellationToken ct)
    {
        using var guard = await locks.AcquireAsync(chatId, ct);
        const string error = "Не удалось прочитать или сохранить историю. Попробуйте позже.";
        var user = await Storage(() => store.GetOrCreateAsync(chatId, ct), error);
        var history = await Storage(() => store.GetHistoryAsync(chatId, settings.HistoryMaxMessages, ct), error);
        var messages = ContextBuilder.Build(user.Mode, history, text, settings.HistoryMaxMessages, settings.ContextMaxChars);
        var response = await llm.GenerateAsync(messages, user.Temperature, ct);
        await Storage(() => store.SaveTurnAsync(chatId, text, response.Text, ct), error);
        return response with
        {
            Mode = user.Mode
        };
    }
    public async Task ResetAsync(long chatId, CancellationToken ct)
    {
        using var guard = await locks.AcquireAsync(chatId, ct);
        await Storage(() => store.ClearHistoryAsync(chatId, ct), "Не удалось очистить историю. Попробуйте позже.");
    }
    public async Task SetModeAsync(long chatId, string mode, CancellationToken ct)
    {
        _ = Modes.Name(mode);
        using var guard = await locks.AcquireAsync(chatId, ct);
        await Storage(() => store.SetModeAsync(chatId, mode, ct), "Не удалось изменить режим. Попробуйте позже.");
    }
    public async Task SetTemperatureAsync(long chatId, double temperature, CancellationToken ct)
    {
        Modes.ValidateTemperature(temperature);
        using var guard = await locks.AcquireAsync(chatId, ct);
        await Storage(() => store.SetTemperatureAsync(chatId, temperature, ct), "Не удалось изменить уровень креативности. Попробуйте позже.");
    }
    public async Task<DialogueSettings> GetSettingsAsync(long chatId, CancellationToken ct)
    {
        using var guard = await locks.AcquireAsync(chatId, ct);
        var user = await Storage(() => store.GetOrCreateAsync(chatId, ct), "Не удалось прочитать настройки. Попробуйте позже.");
        return new(user.Mode, user.Temperature, settings.Model);
    }
}
