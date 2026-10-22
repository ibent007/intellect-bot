using System.Globalization;
using IntellectBot.Domain;

namespace IntellectBot.Agent;

// Приложение сообщает точное локальное время; модель не угадывает сегодняшний день недели.
public static class AgentTimeContext
{
    public static string Rules(DateTimeOffset instant, string? zone)
    {
        var local = zone is null ? instant.ToUniversalTime() :
            TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(zone));
        return Prompts.AgentRules + $"\nТекущее время: {local:O}. " +
            $"День недели: {local.ToString("dddd", CultureInfo.GetCultureInfo("ru-RU"))}. " +
            $"Часовой пояс пользователя: {zone ?? "не задан"}.";
    }
}
