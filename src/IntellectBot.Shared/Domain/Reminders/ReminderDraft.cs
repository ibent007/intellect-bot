namespace IntellectBot.Domain;

public sealed record ReminderDraft(
    string Text,
    DateTimeOffset RemindAt,
    string TimeZone)
{
    public override string ToString() => "ReminderDraft([hidden])";
}

