using IntellectBot.Application;
using IntellectBot.Domain;
using IntellectBot.Agent;
using IntellectBot.Infrastructure;
using IntellectBot.Llm;

namespace IntellectBot.Telegram;

// Последовательность событий одного чата, маршрутизация и безопасная обработка ошибок.
public sealed class UpdateHandler
{
    private readonly ChatLocks locks = new();
    private readonly ITelegramApi api;
    private readonly SafeLog log;
    private readonly MessageRouter messages;
    private readonly CallbackRouter callbacks;

    public UpdateHandler(ITelegramApi api, IDialogueService dialogue, SafeLog log,
        IAgentUiService? agentUi = null, WorkspaceController? workspace = null, InteractionState? interactions = null)
    {
        this.api = api;
        this.log = log;
        var state = interactions ?? new InteractionState(TimeProvider.System);
        messages = new(api, dialogue, agentUi, workspace, state);
        callbacks = new(api, dialogue, agentUi, workspace, state);
    }

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
                await callbacks.HandleAsync(callback, ct);
            else if (update.Message is { Chat.Type: "private" } message)
                await messages.HandleAsync(message, update.UpdateId, BotUsername, ct);
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
}
