using System.Text.Json;
using IntellectBot.Domain;
using IntellectBot.Mcp;

namespace IntellectBot.Agent;

public sealed class AgentUiService(IAgentStateStore state, IReminderLifecycle reminders,
    Func<IConfirmedReminderGateway?> gateway, TimeProvider clock) : IAgentUiService
{
    public Task<string?> GetTimeZoneAsync(long owner, CancellationToken ct) => state.GetTimeZoneAsync(owner, ct);
    public Task RecordAsync(long owner, string reason, CancellationToken ct) => RecordActionAsync(owner, "respond", reason, ct);
    public Task RecordActionAsync(long owner, string action, string reason, CancellationToken ct) => state.SaveAuditAsync(owner,
        new(Guid.NewGuid(), clock.GetUtcNow(), action, [], reason), ct);

    public string ToolsText()
    {
        var mcp = gateway();

        if (mcp is null)
            return "Инструменты сейчас недоступны. Обычные вопросы можно задавать как обычно.";

        var descriptions = mcp.Catalog.Definitions.Select(tool =>
            tool.Name + (tool.ReadOnly ? " — чтение данных\n" : " — запись после подтверждения\n") +
            tool.Description);

        return "Доступные инструменты\n\n" +
            string.Join("\n\n", descriptions) +
            "\n\nРесурс: расписание текущей недели.\n\n" +
            "Не более двух действий на сообщение. " +
            "Уведомления о напоминаниях пока не отправляются.";
    }

    public async Task<string> TimeZoneAsync(long owner, string? value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            var zone = await state.GetTimeZoneAsync(owner, ct);
            return $"Часовой пояс: {zone ?? "не задан"}.\n\nИзменить: Настройки → Часовой пояс → Другой пояс.\nПример: Europe/Berlin или Asia/Tokyo.";
        }
        try
        {
            var zone = AgentStateValidation.ValidateTimeZone(value);
            await state.SetTimeZoneAsync(owner, zone, ct);
            await RecordActionAsync(owner, "settings", "Пользователь изменил часовой пояс. Новые запросы используют выбранный пояс.", ct);
            return $"Часовой пояс сохранён: {zone}.\nРасписание и новые напоминания будут использовать этот пояс. Ранее подготовленные сохраняют своё время и пояс.";
        }
        catch (ArgumentException)
        {
            return "Неизвестный часовой пояс. Укажите название IANA, например Europe/Moscow.";
        }
    }

    public async Task<string> WhyAsync(long owner, CancellationToken ct)
    {
        var audit = await state.GetLastAuditAsync(owner, ct);

        if (audit is null)
            return "Пока нет обработанного запроса для объяснения. Сначала задайте вопрос.";

        var zone = await state.GetTimeZoneAsync(owner, ct);
        var time = zone is null
            ? audit.CreatedAt.ToUniversalTime()
            : TimeZoneInfo.ConvertTime(
                audit.CreatedAt,
                TimeZoneInfo.FindSystemTimeZoneById(zone));

        var action = audit.Action switch
        {
            "call_tool" => "вызов инструмента",
            "clarify" => "уточнение",
            _ => "ответ без инструмента"
        };

        var lines = new List<string>
        {
            $"Последний запрос: {time:dd.MM.yyyy HH:mm}",
            "Часовой пояс:",
            zone ?? "UTC — пояс не задан",
            $"Действие: {action}"
        };

        if (audit.Tools.Length == 0)
        {
            lines.Add("Инструмент: не выбирался");
            lines.Add("Аргументы: нет");
            lines.Add("Проверка: не требовалась");
            lines.Add("Выполнение: инструмент не вызывался");
        }

        foreach (var tool in audit.Tools)
        {
            var name = tool.Name switch
            {
                "get_weather" => "get_weather (Погода)",
                "get_schedule" => "get_schedule (Расписание)",
                "get_reference" => "get_reference (Справка по источнику)",
                "add_reminder" => "add_reminder (Напоминание)",
                "unknown_tool" => "неизвестный инструмент — отклонён",
                _ => tool.Name
            };

            var validation = tool.Validation switch
            {
                "passed" => "пройдена",
                "rejected" => "отклонена",
                _ => "не требовалась"
            };

            var execution = tool.Execution switch
            {
                "success" => "успешно",
                "failed" => "ошибка",
                "pending_confirmation" => "ожидает подтверждения",
                "already_exists" => "уже существует; дубликат не создан",
                "cancelled" => "отменено",
                "expired" => "срок подтверждения истёк",
                "limit_exceeded" => "достигнут лимит",
                _ => "не выполнялось"
            };

            lines.Add($"Инструмент: {name}");
            lines.Add($"Аргументы: {DisplayTime.Simplify(tool.ArgumentsSummary)}");
            lines.Add($"Проверка: {validation}");
            lines.Add($"Выполнение: {execution}");
        }

        lines.Add($"Причина: {audit.Reason}");
        return string.Join("\n", lines);
    }

    public Task<ReminderUiResult> ReminderAsync(long owner, Guid actionId, bool confirm, CancellationToken ct) =>
        new ReminderConfirmationService(state, reminders, gateway, clock).ReminderAsync(owner, actionId, confirm, ct);
}
