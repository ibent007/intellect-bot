namespace IntellectBot.Domain;

public sealed class ReminderException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}
