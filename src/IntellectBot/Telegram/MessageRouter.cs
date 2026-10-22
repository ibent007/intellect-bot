using IntellectBot.Application;
using IntellectBot.Domain;
using IntellectBot.Agent;
using IntellectBot.Infrastructure;
using IntellectBot.Llm;
using System.Text.RegularExpressions;

namespace IntellectBot.Telegram;

// Разбирает команды и обычный текст; вызывает сценарии, но не выполняет SQL.
internal sealed class MessageRouter(ITelegramApi api, IDialogueService dialogue, IAgentUiService? agentUi, WorkspaceController? workspace, InteractionState interactions)
{
    private readonly SettingsController settingsController = new(api, dialogue, agentUi, interactions);
    public async Task HandleAsync(TelegramMessage message, long updateId, string? botUsername, CancellationToken ct)
    {
        var id = message.Chat.Id;
        if (message.Text is null)
        {
            await Send(MenuView.UnsupportedText);
            return;
        }
        var text = message.Text;
        var first = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var command = first.Split('@', 2);
        var addressed = command.Length == 1 || string.Equals(command[1], botUsername, StringComparison.OrdinalIgnoreCase);
        try
        {
            if (text.StartsWith('/') && addressed)
            {
                if (command[0] is "/start" or "/settings" or "/reset")
                {
                    workspace?.Cancel(id);
                    interactions.Clear(id);
                }
                switch (command[0])
                {
                    case "/why":
                        await Send(agentUi is null ? "Объяснение пока недоступно." : await agentUi.WhyAsync(id, ct));
                        return;
                    case "/start":
                        await Send(MenuView.StartText, MenuView.MainKeyboard());
                        return;
                    case "/reset":
                        await dialogue.ResetAsync(id, ct);
                        if (agentUi is not null)
                            await agentUi.RecordActionAsync(id, "reset", "Пользователь очистил историю диалога. Настройки и личные данные сохранены.", ct);
                        await Send(MenuView.ResetText);
                        return;
                    case "/settings":
                        var settings = await dialogue.GetSettingsAsync(id, ct);
                        await Send(MenuView.SettingsText(settings, agentUi is null ? null : await agentUi.GetTimeZoneAsync(id, ct)), MenuView.SettingsKeyboard(settings, false));
                        return;
                }
            }
            if (text.StartsWith('/'))
            {
                await Send(MenuView.UnknownText);
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                await Send(MenuView.EmptyText);
                return;
            }
            if (await settingsController.AcceptAsync(id, text, ct))
                return;
            if (workspace?.AwaitingInput(id) == true)
            {
                var preview = workspace.Input(id, text);
                await Send(preview.Text, preview.Keyboard);
                return;
            }
            if (Regex.IsMatch(text.Trim(), @"^(?:да|подтверждаю|подтвердить|согласен|согласна)[.!]*$", RegexOptions.IgnoreCase) &&
                interactions.PendingConfirmations(id) is { Count: > 0 } waiting)
            {
                await Send("Для сохранения нажмите «Подтвердить» под напоминанием. Текстовое сообщение не создаёт запись.");
                foreach (var action in waiting)
                    await Send(ReminderView.PreparedText(action), ReminderView.Keyboard(action.Id));
                return;
            }
            await TryTypingAsync(id, ct);
            using var typingCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var typing = RepeatTypingAsync(id, typingCancellation.Token);
            LlmResponse response;
            try
            {
                response = dialogue is IUpdateDialogueService updateDialogue
                    ? await updateDialogue.ReplyToUpdateAsync(id, updateId, text, ct)
                    : await dialogue.ReplyAsync(id, text, ct);
            }
            finally { await typingCancellation.CancelAsync(); await typing; }
            if (response.PendingActions.Count > 0)
            {
                interactions.RememberConfirmations(id, response.PendingActions);
                if (response.IncludeTextWithPendingActions)
                    foreach (var part in MessageFormatter.Format(response))
                        await api.SendAsync(id, part.Text, null, part.ParseMode, ct);
                foreach (var pending in response.PendingActions)
                    await Send(ReminderView.PreparedText(pending), ReminderView.Keyboard(pending.Id));
                return;
            }
            foreach (var part in MessageFormatter.Format(response))
                await api.SendAsync(id, part.Text, null, part.ParseMode, ct);
        }
        catch (LlmException error) { await Send(error.Message); }
        catch (DialogueException error) { await Send(error.Message); }
        Task Send(string value, InlineKeyboard? keyboard = null) => api.SendAsync(id, value, keyboard, null, ct);
    }
    private async Task TryTypingAsync(long id, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await api.TypingAsync(id, timeout.Token);
        }
        catch (Exception error) when (error is TelegramException or HttpRequestException or OperationCanceledException) { }
    }
    private async Task RepeatTypingAsync(long id, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                await TryTypingAsync(id, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
