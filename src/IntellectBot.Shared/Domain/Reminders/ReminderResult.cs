namespace IntellectBot.Domain;

public sealed record ReminderResult(
    Guid Id,
    DateTimeOffset RemindAt,
    string TimeZone,
    bool Created)
{
    public override string ToString() => "ReminderResult([hidden])";
}

