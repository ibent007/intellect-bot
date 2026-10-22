using System.Text.Json;
using IntellectBot.Domain;
using IntellectBot.Mcp;
namespace IntellectBot.Agent;

// Подтверждение, отмена и повторная проверка сохранения напоминаний
public sealed class ReminderConfirmationService(IAgentStateStore state, IReminderLifecycle reminders,
    Func<IConfirmedReminderGateway?> gateway, TimeProvider clock)
{
    public async Task<ReminderUiResult> ReminderAsync(long owner, Guid actionId, bool confirm, CancellationToken ct)
    {
        var action = await reminders.GetAsync(owner, actionId, ct);
        if (action is null)
            return new("Действие не найдено или принадлежит другому пользователю.");
        if (action.Status == "executed")
            return await Finished("Напоминание уже сохранено.", "success", "Повторное нажатие не создало дубликат.");
        if (action.Status == "cancelled")
            return await Finished("Создание напоминания отменено.", "cancelled", "Действие уже отменено.");
        if (action.Status == "expired")
            return await Finished("Срок подтверждения истёк. Отправьте новый запрос.", "expired", "Срок подтверждения истёк.");
        if (!confirm)
        {
            try
            {
                await reminders.CancelAsync(owner, actionId, ct);
            }
            catch (ReminderException error) when (error.Code == "already_executed")
            {
                return await Finished("Напоминание уже сохранено.", "success", "Подтверждение завершилось раньше отмены.");
            }
            return await Finished("Создание напоминания отменено.", "cancelled", "Пользователь отменил подготовленное действие.");
        }
        try
        {
            action = await reminders.ConfirmAsync(owner, actionId, ct);
            if (action.Status == "executed")
                return await Finished("Напоминание уже сохранено.", "success", "Повторное нажатие не создало дубликат.");
            var mcp = gateway();
            if (mcp is null)
                return await Retry();
            var result = await mcp.ExecuteConfirmedReminderAsync(owner, action, ct);
            if (result.Success && ValidResult(result.Data, action))
                return await Finished(result.Data!.Value.GetProperty("created").GetBoolean()
                    ? "Напоминание сохранено." : "Такое напоминание уже сохранено. Повторная запись не создана.",
                    "success", "Пользователь подтвердил точные параметры; результат сохранения проверен через MCP.");

            // Ответ RPC мог потеряться уже после фиксации транзакции.
            var persisted = await reminders.GetAsync(owner, actionId, ct);
            if (persisted?.Status == "executed")
                return await Finished("Напоминание сохранено.", "success", "Сохранение подтверждено состоянием PostgreSQL после потери ответа MCP.");
            if (persisted?.Status == "expired")
                return await Finished("Срок подтверждения истёк. Отправьте новый запрос.", "expired", "Срок подтверждения истёк.");
            if (persisted?.Status == "cancelled")
                return await Finished("Создание напоминания отменено.", "cancelled", "Действие отменено.");
            return await Retry();
        }
        catch (ReminderException error)
        {
            var execution = error.Code switch
            {
                "expired" => "expired",
                "cancelled" => "cancelled",
                _ => "failed"
            };
            return await Finished(error.Message, execution, "Подтверждение отклонено; действие не выполнено.");
        }

        async Task<ReminderUiResult> Retry()
        {
            await Audit("failed", "Создание через MCP не завершено; требуется повторное нажатие пользователя.");
            return new("Не удалось завершить сохранение. Можно повторить подтверждение до истечения срока или отменить действие.", true);
        }
        async Task<ReminderUiResult> Finished(string title, string execution, string reason)
        {
            await Audit(execution, reason);
            return new(title + "\n\n" + ReminderDetails(action) +
                (execution == "success" ? "\n\nУведомления пока не отправляются." : ""));
        }
        Task Audit(string execution, string reason) => state.SaveAuditAsync(owner,
            new AgentAuditEntry(Guid.NewGuid(), clock.GetUtcNow(), "call_tool",
                [new ToolAuditEntry("add_reminder",
                    ReminderDisplay.Summary(action.Draft),
                    execution == "expired" ? "rejected" : "passed", execution)], reason), ct);
    }

    public static string ReminderDetails(PreparedReminder action)
    {
        var local = TimeZoneInfo.ConvertTime(action.Draft.RemindAt, TimeZoneInfo.FindSystemTimeZoneById(action.Draft.TimeZone));
        return $"Текст: {action.Draft.Text}\nВремя: {local:dd.MM.yyyy HH:mm}\nЧасовой пояс: {action.Draft.TimeZone}";
    }

    private static bool ValidResult(JsonElement? data, PreparedReminder action)
    {
        try
        {
            if (data is not { ValueKind: JsonValueKind.Object } value ||
                !Guid.TryParse(value.GetProperty("id").GetString(), out var id) || id == Guid.Empty ||
                value.GetProperty("created").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                value.GetProperty("timezone").GetString() != action.Draft.TimeZone)
                return false;
            var normalized = ReminderValidation.Parse(action.Draft.Text,
                value.GetProperty("remind_at").GetString(), action.Draft.TimeZone);
            return normalized == action.Draft;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ReminderException)
        {
            return false;
        }
    }
}
