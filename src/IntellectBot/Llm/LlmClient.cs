using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IntellectBot.Configuration;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;

namespace IntellectBot.Llm;

public sealed class LlmException(string code) : Exception(MessageFor(code))
{
    public string Code { get; } = code;
    public static string MessageFor(string code) => code switch
    {
        "timeout" => "Модель не успела ответить. Попробуйте ещё раз чуть позже.",
        "authorization" => "Не удалось подключиться к сервису ИИ. Сообщите администратору.",
        "rate_limit" => "Достигнут лимит запросов к ИИ. Попробуйте позже.",
        "invalid_response" => "Модель вернула некорректный ответ. Попробуйте ещё раз.",
        "empty_response" => "Ответ модели пуст. Попробуйте переформулировать вопрос.",
        "incomplete_response" => "Ответ модели не завершён. Попробуйте задать более узкий вопрос.",
        "invalid_temperature" => Modes.InvalidTemperature,
        _ => "Сервис ИИ временно недоступен. Попробуйте позже."
    };
}
public sealed class LlmClient(HttpClient http, LlmSettings settings, SafeLog log) : ILlmClient
{
    public async Task<LlmResponse> GenerateAsync(IReadOnlyList<ChatMessage> messages, double temperature, CancellationToken cancellationToken)
    {
        if (!Modes.Temperatures.Contains(temperature))
            throw new LlmException("invalid_temperature");
        var id = Guid.NewGuid().ToString("N");
        var clock = Stopwatch.StartNew();
        log.Write($"Начало вызова LLM: request_id={id} model={settings.Model}");
        try
        {
            var payload = new Dictionary<string, object>
            {
                ["model"] = settings.Model,
                ["messages"] = messages,
                ["temperature"] = temperature,
                ["reasoning_effort"] = settings.ReasoningEffort,
                ["max_completion_tokens"] = settings.MaxCompletionTokens
            };
            if (settings.Model == "qwen/qwen3.8-27b")
                payload["reasoning_format"] = "hidden";
            else
                payload["include_reasoning"] = false;
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl + "/chat/completions") { Content = JsonContent.Create(payload) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new LlmException("authorization");
            if ((int)response.StatusCode == 429)
                throw new LlmException("rate_limit");
            if (response.StatusCode != HttpStatusCode.OK)
                throw new LlmException("unavailable");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType != "application/json" && !(mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ?? false))
                throw new LlmException("invalid_response");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var result = Parse(json.RootElement);
            log.Write($"Успешный вызов LLM: request_id={id} model={settings.Model} duration_ms={clock.ElapsedMilliseconds}");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Failure("timeout"); }
        catch (HttpRequestException) { throw Failure("unavailable"); }
        catch (IOException) { throw Failure("unavailable"); }
        catch (JsonException) { throw Failure("invalid_response"); }
        catch (LlmException error) { throw Failure(error.Code); }
        LlmException Failure(string code)
        {
            log.Write($"Ошибка вызова LLM: request_id={id} model={settings.Model} code={code} duration_ms={clock.ElapsedMilliseconds}", true);
            return new(code);
        }
    }
    public static LlmResponse Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new LlmException("invalid_response");
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("finish_reason", out var reason) || reason.ValueKind != JsonValueKind.String)
            throw new LlmException("invalid_response");
        if (reason.GetString() == "length")
            throw new LlmException("incomplete_response");
        if (reason.GetString() != "stop" || !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content))
            throw new LlmException("invalid_response");
        if (content.ValueKind == JsonValueKind.Null)
            throw new LlmException("empty_response");
        if (content.ValueKind != JsonValueKind.String)
            throw new LlmException("invalid_response");
        var text = content.GetString()!;
        if (string.IsNullOrWhiteSpace(text))
            throw new LlmException("empty_response");
        int? TokenCount(string name) => data.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n) && n >= 0 ? n : null;
        return new(text, TokenCount("prompt_tokens"), TokenCount("completion_tokens"));
    }
}
