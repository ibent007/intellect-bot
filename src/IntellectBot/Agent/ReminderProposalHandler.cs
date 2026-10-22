using IntellectBot.Mcp;
using IntellectBot.Domain;
using System.Text.Json;

namespace IntellectBot.Agent;

// Доверенный адаптер записи. Новый изменяющий метод подключается отдельным адаптером.
public interface IToolProposalHandler
{
    string ToolName
    {
        get;
    }
    Task<PreparedReminder> PrepareAsync(long owner, long updateId, int index,
        JsonElement arguments, string? timeZone, CancellationToken ct);
}

internal sealed class ReminderProposalHandler(IReminderPreparation preparation, TimeProvider clock) : IToolProposalHandler
{
    public string ToolName => "add_reminder";

    public async Task<PreparedReminder> PrepareAsync(long owner, long updateId, int index,
        JsonElement arguments, string? timeZone, CancellationToken ct)
    {
        if (timeZone is null)
            throw new ReminderException("timezone_required",
                "Для напоминания выберите часовой пояс: Настройки → Часовой пояс.");
        var draft = ReminderValidation.Parse(arguments.GetProperty("text").GetString(),
            arguments.GetProperty("remind_at").GetString(), timeZone);
        ReminderValidation.RequireFuture(draft, clock.GetUtcNow());
        var pending = await preparation.PrepareAsync(owner, updateId, index,
            draft.Text, ReminderAuthorization.FormatTime(draft.RemindAt), ct);
        if (pending.Status is not ("pending" or "confirmed"))
            throw new ReminderException("inactive_action",
                "Это действие уже завершено. Отправьте новый запрос.");
        return pending;
    }
}
