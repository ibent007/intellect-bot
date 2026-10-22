namespace IntellectBot.Domain;

public sealed record UserSettings(
    string Mode = "chat",
    double Temperature = 0.3,
    string? Model = null);
