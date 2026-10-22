using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace IntellectBot.Llm;

// Последовательный доступ к квоте одной модели. Другие модели не блокируются.
internal sealed class LlmRequestCoordinator(TimeProvider clock, Func<TimeSpan, CancellationToken, Task>? delayWait = null)
{
    internal sealed class Window
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset BlockedUntil
        {
            get; set;
        }
        public DateTimeOffset TokensResetAt
        {
            get; set;
        }
        public DateTimeOffset RequestsResetAt
        {
            get; set;
        }
        public long? RemainingTokens
        {
            get; set;
        }
        public long? RemainingRequests
        {
            get; set;
        }

        public DateTimeOffset ReadyAt(DateTimeOffset now, int estimatedTokens)
        {
            var ready = BlockedUntil;
            if (RemainingRequests == 0 && RequestsResetAt > ready)
                ready = RequestsResetAt;
            if (RemainingTokens == 0 && TokensResetAt > ready)
                ready = TokensResetAt;
            return ready > now ? ready : now;
        }
    }

    private readonly ConcurrentDictionary<string, Window> windows = new(StringComparer.Ordinal);
    public Window For(string key) => windows.GetOrAdd(key, _ => new());

    public async Task WaitAsync(Window window, int estimatedTokens, TimeSpan maxWait, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var delay = window.ReadyAt(now, estimatedTokens) - now;
        if (delay <= TimeSpan.Zero)
            return;
        if (delay > maxWait)
            throw new LlmException("rate_limit") { RetryAfter = delay };
        if (delayWait is null)
            await Task.Delay(delay, clock, ct);
        else
            await delayWait(delay, ct);
    }

    public void Observe(Window window, HttpResponseHeaders headers, bool limited)
    {
        var now = clock.GetUtcNow();
        window.RemainingTokens = Number(headers, "x-ratelimit-remaining-tokens");
        window.RemainingRequests = Number(headers, "x-ratelimit-remaining-requests");
        window.TokensResetAt = now + (Duration(Header(headers, "x-ratelimit-reset-tokens")) ?? TimeSpan.Zero);
        window.RequestsResetAt = now + (Duration(Header(headers, "x-ratelimit-reset-requests")) ?? TimeSpan.Zero);
        if (limited)
        {
            var delay = RetryAfter(headers, now);
            if (delay is null && window.RemainingRequests == 0)
                delay = window.RequestsResetAt - now;
            if (delay is null && window.RemainingTokens == 0)
                delay = window.TokensResetAt - now;
            window.BlockedUntil = now + (delay is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
        }
        else
            window.BlockedUntil = now;
    }

    internal static TimeSpan? RetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        var delay = headers.RetryAfter?.Delta ??
            (headers.RetryAfter?.Date is { } at ? at - now : null);
        return delay is { } value && value >= TimeSpan.Zero ? value : null;
    }

    internal static TimeSpan? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80)
            return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return double.IsFinite(seconds) && seconds is >= 0 and <= 86400 ? TimeSpan.FromSeconds(seconds) : null;
        var matches = Regex.Matches(text, @"(\d+(?:\.\d+)?)(ms|h|m|s)");
        if (matches.Count == 0 || string.Concat(matches.Select(match => match.Value)) != text)
            return null;
        var total = matches.Sum(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) *
            (match.Groups[2].Value switch
            {
                "h" => 3600,
                "m" => 60,
                "ms" => .001,
                _ => 1
            }));
        return double.IsFinite(total) && total is >= 0 and <= 86400 ? TimeSpan.FromSeconds(total) : null;
    }

    private static string? Header(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    private static long? Number(HttpResponseHeaders headers, string name) =>
        long.TryParse(Header(headers, name), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;
}
