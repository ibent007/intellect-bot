using System.Text.Json;
using IntellectBot.Configuration;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;

namespace IntellectBot.Llm;

// Обычный текстовый диалог использует тот же транспорт и квоту, что и агент.
public sealed class LlmClient(HttpClient http, LlmSettings settings, SafeLog log) : ILlmClient
{
    private readonly ChatCompletionTransport transport = new(http, settings, log);

    public async Task<LlmResponse> GenerateAsync(IReadOnlyList<ChatMessage> messages, double temperature,
        CancellationToken cancellationToken, string? model = null)
    {
        if (!Modes.Temperatures.Contains(temperature))
            throw new LlmException("invalid_temperature");
        var selected = LlmModels.Resolve(model, settings.Model);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = selected,
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["max_completion_tokens"] = settings.MaxCompletionTokens
        };
        ModelCapabilities.Apply(payload, selected, settings.ReasoningEffort);
        var reply = await transport.SendAsync(payload, cancellationToken);
        try
        {
            return Parse(reply.Data);
        }
        catch (LlmException error)
        {
            log.Write($"Ответ LLM отклонён: request_id={reply.RequestId} model={selected} code={error.Code}", true);
            throw;
        }
    }

    public static LlmResponse Parse(JsonElement data) => CompletionParser.Text(data);
}
