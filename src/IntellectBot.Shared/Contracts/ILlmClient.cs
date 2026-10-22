namespace IntellectBot.Domain;

public interface ILlmClient
{
    Task<LlmResponse> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        double temperature,
        CancellationToken cancellationToken,
        string? model = null);
}
