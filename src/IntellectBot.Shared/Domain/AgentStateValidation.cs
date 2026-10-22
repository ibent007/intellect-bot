using System.Text.RegularExpressions;

namespace IntellectBot.Domain;

public static class AgentStateValidation
{
    public static void ValidateOwner(long chatId)
    {
        if (chatId <= 0)
            throw new ArgumentException(
                "Требуется владелец из личного Telegram-чата.");
    }

    public static string ValidateTimeZone(string? value)
    {
        var name = value?.Trim() ?? "";

        if (name.Length is < 1 or > 100 ||
            !TimeZoneInfo.TryConvertIanaIdToWindowsId(name, out _))
        {
            throw new ArgumentException(
                "Укажите существующий IANA-часовой пояс, " +
                "например Europe/Moscow.");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException(
                "Этот часовой пояс недоступен.");
        }

        return name;
    }

    public static void ValidateAudit(AgentAuditEntry audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

        if (audit.RequestId == Guid.Empty ||
            audit.CreatedAt == default ||
            audit.Action is not ("respond" or "clarify" or "call_tool" or "settings" or "reset" or "schedule_read" or "schedule_write"))
        {
            throw new ArgumentException("Некорректные поля аудита.");
        }

        if (audit.Tools is null || audit.Tools.Length > 2)
            throw new ArgumentException(
                "Аудит допускает не более двух вызовов.");

        if (audit.Action == "call_tool" && audit.Tools.Length == 0)
            throw new ArgumentException(
                "Для действия call_tool требуется запись инструмента.");

        CheckText(audit.Reason, 300);

        foreach (var tool in audit.Tools)
        {
            if (tool is null ||
                tool.Name is null ||
                !Regex.IsMatch(
                    tool.Name, @"^[A-Za-z0-9_.-]{1,100}$"))
            {
                throw new ArgumentException(
                    "Некорректное имя инструмента в аудите.");
            }

            CheckText(tool.ArgumentsSummary, 500);

            if (tool.Validation is not (
                "passed" or "rejected" or "not_required"))
            {
                throw new ArgumentException(
                    "Некорректный статус проверки.");
            }

            if (tool.Execution is not (
                "success" or "failed" or "not_called" or
                "pending_confirmation" or "cancelled" or
                "expired" or "limit_exceeded" or "already_exists"))
            {
                throw new ArgumentException(
                    "Некорректный статус выполнения.");
            }
        }
    }

    private static void CheckText(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException(
                "Текст аудита пуст или превышает допустимую длину.");
    }
}
