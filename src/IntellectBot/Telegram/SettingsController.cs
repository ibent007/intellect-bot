using IntellectBot.Agent;
using IntellectBot.Application;
using IntellectBot.Domain;

namespace IntellectBot.Telegram;

// Настройки и ввод собственного часового пояса без передачи служебного ввода модели.
internal sealed class SettingsController(ITelegramApi api, IDialogueService dialogue, IAgentUiService? agentUi, InteractionState interactions)
{
    public async Task<WorkspaceScreen> ScreenAsync(long owner, bool showMenuBack, CancellationToken ct)
    {
        var settings = await dialogue.GetSettingsAsync(owner, ct);
        var zone = agentUi is null ? null : await agentUi.GetTimeZoneAsync(owner, ct);
        return new(MenuView.SettingsText(settings, zone), MenuView.SettingsKeyboard(settings, showMenuBack));
    }

    public WorkspaceScreen Begin(long owner, long messageId, bool showMenuBack)
    {
        if (agentUi is null)
            return new("Настройка часового пояса сейчас недоступна.", MenuView.TimeZoneKeyboard(showMenuBack));
        var input = interactions.BeginTimeZone(owner, messageId, showMenuBack);
        return new("Ваш часовой пояс\n\nОтправьте название в формате Регион/Город.\nНапример: Asia/Yekaterinburg или Europe/Berlin.\n\nДля выхода нажмите «Отмена».", CancelKeyboard(input));
    }

    public async Task<bool> AcceptAsync(long owner, string text, CancellationToken ct)
    {
        var input = interactions.GetTimeZone(owner);
        if (input is null)
            return false;
        if (interactions.IsExpired(input))
        {
            interactions.ClearTimeZone(owner);
            await api.SendAsync(owner, "Время ввода истекло. Откройте «Часовой пояс» в настройках ещё раз.", null, null, ct);
            return true;
        }
        string zone;
        try
        {
            zone = AgentStateValidation.ValidateTimeZone(text.Trim());
        }
        catch (ArgumentException)
        {
            await api.SendAsync(owner, "Такой часовой пояс не найден. Пример: Asia/Yekaterinburg. Попробуйте ещё раз или нажмите «Отмена».", CancelKeyboard(input), null, ct);
            return true;
        }
        if (agentUi is null)
            return false;
        await agentUi.TimeZoneAsync(owner, zone, ct);
        interactions.ClearTimeZone(owner);
        var screen = await ScreenAsync(owner, input.ShowMenuBack, ct);
        await api.EditAsync(owner, input.MessageId, screen.Text, screen.Keyboard, ct);
        await api.SendAsync(owner, $"Часовой пояс изменён на {zone}.", null, null,
            ct);
        return true;
    }

    public async Task CancelAsync(CallbackQuery callback, CancellationToken ct)
    {
        var message = callback.Message!;
        var input = interactions.GetTimeZone(message.Chat.Id);
        if (input is null || !Guid.TryParseExact(callback.Data![12..], "N", out var id) || input.Id != id)
        {
            await api.AnswerCallbackAsync(callback.Id, "Этот ввод уже завершён. Откройте настройки.", true, ct);
            return;
        }
        interactions.ClearTimeZone(message.Chat.Id);
        await api.AnswerCallbackAsync(callback.Id, null, false, ct);
        var screen = await ScreenAsync(message.Chat.Id, input.ShowMenuBack, ct);
        await api.EditAsync(message.Chat.Id, input.MessageId, screen.Text, screen.Keyboard, ct);
        if (message.MessageId != input.MessageId)
            await api.EditAsync(message.Chat.Id, message.MessageId, "Ввод отменён.", new InlineKeyboard([]), ct);
    }

    private static InlineKeyboard CancelKeyboard(TimeZoneInput input) => new([
        new InlineButton[] { new("Отмена", $"zone:cancel:{input.Id:N}") }
    ]);
}
