using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntellectBot.Configuration;
using IntellectBot.Infrastructure;

namespace IntellectBot.Llm;

// Общая HTTP-граница: квоты, таймаут, ограничение размера, безопасные ошибки и журнал.
internal sealed class ChatCompletionTransport(HttpClient http, LlmSettings settings, SafeLog log, LlmRequestCoordinator? requestCoordinator = null)
{
    private static readonly ConditionalWeakTable<HttpClient, LlmRequestCoordinator> Coordinators = new();
    private readonly LlmRequestCoordinator coordinator = requestCoordinator ?? Coordinators.GetValue(http,
        _ => new LlmRequestCoordinator(TimeProvider.System));

    public async Task<Completion> SendAsync(Dictionary<string, object?> payload, CancellationToken ct)
    {
        var model = (string)payload["model"]!;
        var key = settings.BaseUrl + ":" + model + ":" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ApiKey)));
        var window = coordinator.For(key);
        // Приближённая оценка нужна только для паузы; это не замена токенизатору провайдера.
        var estimate = Math.Max(1, JsonDefaults.Text(payload).EnumerateRunes().Count() / 2) +
            Math.Min((int)payload["max_completion_tokens"]!, 1024);
        using var queue = CancellationTokenSource.CreateLinkedTokenSource(ct);
        queue.CancelAfter(TimeSpan.FromSeconds(settings.MaxRateWaitSeconds + settings.TimeoutSeconds));
        var queueStarted = Stopwatch.GetTimestamp();
        try
        {
            await window.Gate.WaitAsync(queue.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException("rate_limit");
        }
        var queueMs = Stopwatch.GetElapsedTime(queueStarted).TotalMilliseconds;
        var watch = Stopwatch.StartNew();
        var id = Guid.NewGuid().ToString("N");
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var remainingWait = TimeSpan.FromSeconds(settings.MaxRateWaitSeconds) - watch.Elapsed;
                var waitStarted = Stopwatch.GetTimestamp();
                await coordinator.WaitAsync(window, estimate,
                    remainingWait > TimeSpan.Zero ? remainingWait : TimeSpan.Zero, ct);
                var waitMs = Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
                if (queueMs > 50 || waitMs > 50)
                    log.Write($"Ожидание LLM: request_id={id} model={model} queue_ms={queueMs:F0} quota_ms={waitMs:F0}");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                using var request = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl + "/chat/completions")
                {
                    Content = JsonContent.Create(payload, options: JsonDefaults.Compact)
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
                log.Write($"Начало вызова LLM: request_id={id} model={model} attempt={attempt + 1}");
                var httpStarted = Stopwatch.GetTimestamp();
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var limited = (int)response.StatusCode == 429;
                coordinator.Observe(window, response.Headers, limited);
                if (limited)
                {
                    var delay = LlmRequestCoordinator.RetryAfter(response.Headers, DateTimeOffset.UtcNow);
                    if (attempt == 0 && delay is { } wait && wait >= TimeSpan.Zero &&
                        wait <= TimeSpan.FromSeconds(settings.MaxRateWaitSeconds) - watch.Elapsed)
                    {
                        log.Write($"Ожидание квоты LLM: request_id={id} model={model} seconds={Math.Ceiling(wait.TotalSeconds)}");
                        continue;
                    }
                    throw new LlmException("rate_limit") { RetryAfter = delay };
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new LlmException("authorization");
                var data = await ReadAsync(response, timeout.Token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var apiCode = ErrorField(data, "code");
                    log.Write($"Запрос LLM отклонён: request_id={id} http_status={(int)response.StatusCode} api_code={apiCode}", true);
                    throw new LlmException(response.StatusCode == HttpStatusCode.BadRequest
                        ? "request_rejected" : "unavailable")
                    {
                        ApiCode = apiCode,
                        HttpStatus = (int)response.StatusCode
                    };
                }
                var usage = data.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
                log.Write($"Успешный HTTP-вызов LLM: request_id={id} model={model} duration_ms={watch.ElapsedMilliseconds} http_ms={Stopwatch.GetElapsedTime(httpStarted).TotalMilliseconds:F0} " +
                    $"prompt_tokens={TokenCount(usage, "prompt_tokens")} completion_tokens={TokenCount(usage, "completion_tokens")} " +
                    $"remaining_tokens={window.RemainingTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
                    $"remaining_requests={window.RemainingRequests?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}");
                return new Completion(data, id);
            }
            throw new LlmException("rate_limit");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failure("timeout"); }
        catch (HttpRequestException) { throw Failure("unavailable"); }
        catch (IOException) { throw Failure("unavailable"); }
        catch (JsonException) { throw Failure("invalid_response"); }
        catch (LlmException error)
        {
            log.Write($"Ошибка вызова LLM: request_id={id} model={model} code={error.Code} duration_ms={watch.ElapsedMilliseconds}", true);
            throw;
        }
        finally { window.Gate.Release(); }

        LlmException Failure(string code)
        {
            log.Write($"Ошибка вызова LLM: request_id={id} model={model} code={code} duration_ms={watch.ElapsedMilliseconds}", true);
            return new(code);
        }
    }

    internal sealed record Completion(JsonElement Data, string RequestId);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            // Тело ошибки может быть не JSON. Оно никогда не возвращается пользователю.
            try
            {
                return await ReadJsonAsync(response, ct);
            }
            catch (Exception error) when (error is JsonException or LlmException) { return default; }
        }
        return await ReadJsonAsync(response, ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int maxBytes = 2 * 1024 * 1024;
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type != "application/json" && !(type?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ?? false) ||
            response.Content.Headers.ContentLength > maxBytes)
            throw new LlmException("invalid_response");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct)) != 0)
        {
            if (buffer.Length + count > maxBytes)
                throw new LlmException("invalid_response");
            buffer.Write(bytes, 0, count);
        }
        using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new LlmException("invalid_response");
        return json.RootElement.Clone();
    }

    private static string ErrorField(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error) &&
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 and <= 100 } value &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? value : "unknown";

    private static string TokenCount(JsonElement usage, string name) => usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number &&
        n.TryGetInt32(out var value) && value >= 0
            ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
}
