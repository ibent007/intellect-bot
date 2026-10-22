using IntellectBot.Domain;
namespace IntellectBot.Agent;

public interface IReminderPreparation
{
    Task<PreparedReminder> PrepareAsync(long owner, long updateId, int proposalIndex,
        string text, string remindAt, CancellationToken ct);
}

