using IntellectBot.Agent;
using IntellectBot.Domain;

namespace IntellectBot.Telegram;

public static class ReminderView
{
    public static string PreparedText(PreparedReminder action)
    {
        var expires = TimeZoneInfo.ConvertTime(action.ExpiresAt, TimeZoneInfo.FindSystemTimeZoneById(action.Draft.TimeZone));
        return "Сохранить напоминание?\n\n" + ReminderConfirmationService.ReminderDetails(action) +
            $"\n\nПодтвердите до {expires:HH:mm}.\nНапоминание ещё не создано. После подтверждения запись будет сохранена. Уведомления пока не отправляются.";
    }
    public static InlineKeyboard Keyboard(Guid actionId) => new([new InlineButton[]
    {
        new("✅ Подтвердить", $"reminder:confirm:{actionId:N}"),
        new("✖️ Отмена", $"reminder:cancel:{actionId:N}")
    }]);
}
