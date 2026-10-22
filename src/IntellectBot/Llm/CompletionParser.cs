using System.Text.Json;
using IntellectBot.Agent;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;

namespace IntellectBot.Llm;

// Разбирает только публичный ответ и вызовы. Скрытые рассуждения не сохраняются.
internal static class CompletionParser
{
    public static AgentModelTurn Agent(JsonElement data)
    {
        try
        {
            var choices = data.GetProperty("choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
                throw Invalid();
            var choice = choices[0];
            var reason = choice.GetProperty("finish_reason").GetString();
            if (reason == "length")
                throw new LlmException("incomplete_response");
            if (reason == "stop")
            {
                var response = Text(data);
                if (choice.GetProperty("message").TryGetProperty("tool_calls", out var unexpected) &&
                    unexpected.ValueKind != JsonValueKind.Null &&
                    (unexpected.ValueKind != JsonValueKind.Array || unexpected.GetArrayLength() != 0))
                    throw Invalid();
                return new(JsonDefaults.Element(new
                {
                    role = "assistant",
                    content = response.Text
                }),
                    response.Text, Array.Empty<AgentToolCall>());
            }
            if (reason != "tool_calls")
                throw Invalid();
            var calls = choice.GetProperty("message").GetProperty("tool_calls");
            if (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() is < 1 or > 8)
                throw Invalid();
            var parsed = new List<AgentToolCall>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in calls.EnumerateArray())
            {
                var id = call.GetProperty("id").GetString();
                var function = call.GetProperty("function");
                var name = function.GetProperty("name").GetString();
                var arguments = function.GetProperty("arguments").GetString();
                if (call.GetProperty("type").GetString() != "function" ||
                    !Identifier(id) || !Identifier(name) || !ids.Add(id!) ||
                    arguments is null || arguments.Length > 16000)
                    throw Invalid();
                parsed.Add(new(id!, name!, arguments));
            }
            return new(AgentToolProtocol.CallMessage(parsed), null, parsed)
            {
                Decision = "call_tool"
            };
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw Invalid();
        }
    }

    public static LlmResponse Text(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
            throw Invalid();
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("finish_reason", out var reason) ||
            reason.ValueKind != JsonValueKind.String)
            throw Invalid();
        if (reason.GetString() == "length")
            throw new LlmException("incomplete_response");
        if (reason.GetString() != "stop" || !choice.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content))
            throw Invalid();
        if (content.ValueKind == JsonValueKind.Null)
            throw new LlmException("empty_response");
        if (content.ValueKind != JsonValueKind.String)
            throw Invalid();
        var text = content.GetString()!;
        if (string.IsNullOrWhiteSpace(text))
            throw new LlmException("empty_response");
        int? Tokens(string name) => data.TryGetProperty("usage", out var usage) &&
            usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number &&
            count.TryGetInt32(out var n) && n >= 0 ? n : null;
        return new(text, Tokens("prompt_tokens"), Tokens("completion_tokens"));
    }

    private static bool Identifier(string? value) => value is { Length: >= 1 and <= 100 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    private static LlmException Invalid() => new("invalid_response");
}
