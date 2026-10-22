using System.Globalization;
using IntellectBot.Domain;
using IntellectBot.Agent;
using IntellectBot.Application;

namespace IntellectBot.Telegram;

// Проверяет владельца кнопки и выполняет навигацию или подтверждение.
internal sealed class CallbackRouter(ITelegramApi api, IDialogueService dialogue, IAgentUiService? agentUi,
    WorkspaceController? workspace, InteractionState interactions)
{
    private readonly SettingsController settings = new(api, dialogue, agentUi, interactions);

    public async Task HandleAsync(CallbackQuery callback, CancellationToken ct)
    {
        if (callback.Message is not { Chat.Type: "private", Date: > 0 } message || message.Chat.Id != callback.From.Id)
        {
            await api.AnswerCallbackAsync(callback.Id, "Откройте меню в своём личном чате с ботом.", true, ct);
            return;
        }
        var data = callback.Data ?? "";
        if (data.StartsWith("zone:cancel:", StringComparison.Ordinal))
        {
            await settings.CancelAsync(callback, ct);
            return;
        }
        if (data.StartsWith("workspace:", StringComparison.Ordinal))
        {
            if (workspace is null)
            {
                await api.AnswerCallbackAsync(callback.Id, "Расписание сейчас недоступно.", true, ct);
                return;
            }
            await api.AnswerCallbackAsync(callback.Id, null, false, ct);
            interactions.Clear(message.Chat.Id);
            var screen = await workspace.OpenAsync(message.Chat.Id, data[10..], ct);
            await api.EditAsync(message.Chat.Id, message.MessageId, screen.Text, screen.Keyboard, ct);
            return;
        }
        if (data.StartsWith("reminder:", StringComparison.Ordinal))
        {
            var fields = data.Split(':');
            if (fields.Length != 3 || fields[1] is not ("confirm" or "cancel") ||
                !Guid.TryParseExact(fields[2], "N", out var actionId) || actionId == Guid.Empty || agentUi is null)
            {
                await api.AnswerCallbackAsync(callback.Id, "Эта кнопка недоступна. Отправьте новый запрос.", true, ct);
                return;
            }
            await api.AnswerCallbackAsync(callback.Id, null, false, ct);
            var result = await agentUi.ReminderAsync(message.Chat.Id, actionId, fields[1] == "confirm", ct);
            if (!result.CanRetry)
                interactions.ForgetConfirmation(message.Chat.Id, actionId);
            await api.EditAsync(message.Chat.Id, message.MessageId, result.Text,
                result.CanRetry ? ReminderView.Keyboard(actionId) : new InlineKeyboard([]), ct);
            return;
        }
        if (!(data.StartsWith("menu:", StringComparison.Ordinal) || data.StartsWith("settings:", StringComparison.Ordinal)))
        {
            await api.AnswerCallbackAsync(callback.Id, "Неизвестная кнопка. Откройте /start.", true, ct);
            return;
        }
        var parts = data.Split(':', 2);
        var back = parts[0] == "menu";
        var action = parts[1];
        var validTemperature = Modes.Temperatures.Any(t => action == "temperature:" + MenuView.ButtonTemperature(t));
        var validZone = action.StartsWith("tz:", StringComparison.Ordinal) && MenuView.TimeZones.Any(item => item.Zone == action[3..]);
        var validModel = action.StartsWith("model:", StringComparison.Ordinal) && LlmModels.IsAllowed(action[6..]);
        if (!validTemperature && !validZone && !validModel &&
            action is not ("settings" or "models" or "zone" or "zone_custom" or "schedule") &&
            !(back && action is "home" or "reset" or "help" or "tools"))
        {
            await api.AnswerCallbackAsync(callback.Id, "Неизвестная кнопка. Откройте /start.", true, ct);
            return;
        }
        await api.AnswerCallbackAsync(callback.Id, null, false, ct);
        workspace?.Cancel(message.Chat.Id);
        interactions.Clear(message.Chat.Id);
        WorkspaceScreen resultScreen;
        try
        {
            if (action == "home")
                resultScreen = new(MenuView.StartText, MenuView.MainKeyboard());
            else if (action == "help")
                resultScreen = new(MenuView.HelpText, MenuView.HelpKeyboard());
            else if (action == "tools")
                resultScreen = new(agentUi?.ToolsText() ?? "Инструменты сейчас недоступны.", MenuView.ToolsKeyboard());
            else if (action == "models")
            {
                var current = await dialogue.GetSettingsAsync(message.Chat.Id, ct);

                resultScreen = new(
                    "Модель ИИ\n\nВыберите модель. Модели могут иметь разные лимиты. " +
                    "История диалога сохранится.",
                    MenuView.ModelKeyboard(current, back));
            }
            else if (action == "zone")
            {
                var selectedZone = agentUi is null
                    ? null
                    : await agentUi.GetTimeZoneAsync(message.Chat.Id, ct);

                resultScreen = new(
                    "Часовой пояс\n\n" +
                    "Выберите город с подходящим временем. " +
                    "Если вашего пояса нет, нажмите «Другой пояс».\n\n" +
                    "Сезонный перевод часов учитывается автоматически.",
                    MenuView.TimeZoneKeyboard(back, selectedZone));
            }
            else if (action == "zone_custom")
                resultScreen = settings.Begin(message.Chat.Id, message.MessageId, back);
            else if (action == "schedule")
                resultScreen = workspace is null ? new("Расписание сейчас недоступно.", new([new InlineButton[] { new("Назад", $"{parts[0]}:settings") }])) :
                    await workspace.OpenAsync(message.Chat.Id, $"{parts[0]}:home", ct);
            else if (action == "reset")
            {
                await dialogue.ResetAsync(message.Chat.Id, ct);
                if (agentUi is not null)
                    await agentUi.RecordActionAsync(message.Chat.Id, "reset", "Пользователь очистил историю диалога. Настройки и личные данные сохранены.", ct);
                resultScreen = new(MenuView.ResetText, MenuView.BackKeyboard());
            }
            else
            {
                if (validZone && agentUi is not null)
                    await agentUi.TimeZoneAsync(message.Chat.Id, action[3..], ct);
                if (validModel)
                    await dialogue.SetModelAsync(message.Chat.Id, action[6..], ct);
                if (validTemperature)
                {
                    await dialogue.SetTemperatureAsync(message.Chat.Id, double.Parse(action[12..], CultureInfo.InvariantCulture), ct);
                    if (agentUi is not null)
                        await agentUi.RecordActionAsync(message.Chat.Id, "settings", "Пользователь изменил уровень креативности. История диалога сохранена.", ct);
                }
                resultScreen = await settings.ScreenAsync(message.Chat.Id, back, ct);
            }
        }
        catch (DialogueException error)
        {
            resultScreen = new(error.Message, message.ReplyMarkup ?? (back ? MenuView.BackKeyboard() : new InlineKeyboard([])));
        }
        await api.EditAsync(message.Chat.Id, message.MessageId, resultScreen.Text, resultScreen.Keyboard, ct);
    }
}
