using System.Globalization;

namespace IntellectBot.McpServer.Schedule;

// Фиксированное время разрешено только обезличенному источнику для автономных проверок.
// Часы личного расписания и проверки подписей этим параметром не меняются.
internal static class DemoScheduleClock
{
    public static TimeProvider FromValue(string? value, TimeProvider fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!DateTimeOffset.TryParseExact(value,
            ["O", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss'Z'"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
            throw new InvalidOperationException("Schedule:ReferenceTime: требуется дата ISO 8601 со смещением.");
        return new FixedClock(instant);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
