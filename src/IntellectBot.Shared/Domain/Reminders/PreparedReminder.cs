namespace IntellectBot.Domain;

public sealed record PreparedReminder(
    Guid Id,
    ReminderDraft Draft,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ConfirmedAt,
    Guid? ReminderId = null)
{
    public override string ToString() => "PreparedReminder([hidden])";
}

