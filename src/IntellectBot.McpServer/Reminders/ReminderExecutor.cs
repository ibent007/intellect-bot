using IntellectBot.Data;
using IntellectBot.Domain;
using IntellectBot.Mcp;

namespace IntellectBot.McpServer.Reminders;

public interface IReminderExecutor
{
    Task<ReminderResult> ExecuteAsync(
        AuthorizedReminder reminder,
        CancellationToken cancellationToken);
}

public sealed class ReminderExecutor(ReminderStore? store) : IReminderExecutor
{
    public Task<ReminderResult> ExecuteAsync(
        AuthorizedReminder reminder,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            throw new ReminderException(
                "unavailable",
                "Создание напоминаний сейчас недоступно.");
        }

        return store.ExecuteConfirmedAsync(
            reminder.OwnerChatId,
            reminder.ActionId,
            reminder.Draft.Text,
            ReminderAuthorization.FormatTime(reminder.Draft.RemindAt),
            cancellationToken);
    }
}
