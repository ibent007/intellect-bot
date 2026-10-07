using System.Globalization;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;
using IntellectBot.Llm;

namespace IntellectBot.Telegram;

public sealed class UpdateHandler(ITelegramApi api, IDialogueService dialogue, SafeLog log)
{
    private readonly ChatLocks locks = new();
    public string? BotUsername
    {
        get; set;
    }
    public async Task HandleAsync(TelegramUpdate update, CancellationToken ct)
    {
        var chat = update.Message?.Chat ?? update.CallbackQuery?.Message?.Chat;
        using var guard = await locks.AcquireAsync(chat?.Id ?? update.CallbackQuery?.From.Id ?? 0, ct);
        try
        {
            if (update.CallbackQuery is { } callback)
                await HandleCallbackAsync(callback, ct);
            else if (update.Message is { Chat.Type: "private" } message)
                await HandleMessageAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            log.Write($"Ошибка обработки update_id={update.UpdateId} type={error.GetType().Name}", true);
            if (error is TelegramException or HttpRequestException || chat?.Type != "private")
                return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await api.SendAsync(chat.Id, "Не удалось обработать запрос. Попробуйте позже.", null, null, timeout.Token);
            }
            catch (Exception sendError) { log.Failure("Не удалось отправить уведомление", sendError); }
        }
    }
    private async Task HandleMessageAsync(TelegramMessage message, CancellationToken ct)
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
        var addressed = command.Length == 1 || string.Equals(command[1], BotUsername, StringComparison.OrdinalIgnoreCase);
        try
        {
            if (text.StartsWith('/') && addressed)
            {
                switch (command[0])
                {
                    case "/start":
                        await Send(MenuView.StartText, MenuView.MainKeyboard());
                        return;
                    case "/reset":
                        await dialogue.ResetAsync(id, ct);
                        await Send(MenuView.ResetText);
                        return;
                    case "/settings":
                        var settings = await dialogue.GetSettingsAsync(id, ct);
                        await Send(MenuView.SettingsText(settings), MenuView.SettingsKeyboard(settings, false));
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
            await TryTypingAsync(id, ct);
            using var typingCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var typing = RepeatTypingAsync(id, typingCancellation.Token);
            LlmResponse response;
            try
            {
                response = await dialogue.ReplyAsync(id, text, ct);
            }
            finally { await typingCancellation.CancelAsync(); await typing; }
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
    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        if (callback.Data is null || !(callback.Data.StartsWith("menu:", StringComparison.Ordinal) || callback.Data.StartsWith("settings:", StringComparison.Ordinal)))
            return;
        if (callback.Message is not { Chat.Type: "private", Date: > 0 } message || message.Chat.Id != callback.From.Id)
        {
            await api.AnswerCallbackAsync(callback.Id, "Откройте меню в личном чате с ботом.", true, ct);
            return;
        }
        var parts = callback.Data.Split(':', 2);
        var back = parts[0] == "menu";
        var action = parts[1];
        var validTemperature = Modes.Temperatures.Any(t => action == "temperature:" + MenuView.ButtonTemperature(t));
        if (!Modes.Ordered.Contains(action) && !validTemperature && !(back && action is "home" or "settings" or "reset"))
        {
            await api.AnswerCallbackAsync(callback.Id, "Неизвестная кнопка. Откройте /start.", true, ct);
            return;
        }
        await api.AnswerCallbackAsync(callback.Id, null, false, ct);
        string text;
        InlineKeyboard keyboard;
        try
        {
            if (action == "home")
            {
                text = MenuView.StartText;
                keyboard = MenuView.MainKeyboard();
            }
            else if (action == "reset")
            {
                await dialogue.ResetAsync(message.Chat.Id, ct);
                text = MenuView.ResetText;
                keyboard = MenuView.BackKeyboard();
            }
            else
            {
                if (Modes.Ordered.Contains(action))
                    await dialogue.SetModeAsync(message.Chat.Id, action, ct);
                else if (validTemperature)
                    await dialogue.SetTemperatureAsync(message.Chat.Id, double.Parse(action[12..], CultureInfo.InvariantCulture), ct);
                var settings = await dialogue.GetSettingsAsync(message.Chat.Id, ct);
                text = MenuView.SettingsText(settings);
                keyboard = MenuView.SettingsKeyboard(settings, back);
            }
        }
        catch (DialogueException error)
        {
            text = error.Message;
            keyboard = message.ReplyMarkup ?? (back ? MenuView.BackKeyboard() : new InlineKeyboard([]));
        }
        await api.EditAsync(message.Chat.Id, message.MessageId, text, keyboard, ct);
    }
}
