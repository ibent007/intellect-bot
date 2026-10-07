using System.Text.RegularExpressions;
using IntellectBot.Configuration;

namespace IntellectBot.Infrastructure;

public sealed class SafeLog(AppSettings settings)
{
    private readonly string[] secrets = new[] { settings.BotToken, settings.PostgresPassword, settings.Llm.ApiKey, settings.TelegramProxyUrl }.Where(s => s.Length > 0).OrderByDescending(s => s.Length).ToArray();
    private readonly object sync = new();
    public string Redact(string value)
    {
        foreach (var secret in secrets)
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        value = Regex.Replace(value, @"\b\d{5,}:[A-Za-z0-9_-]+", "[REDACTED]");
        value = Regex.Replace(value, @"(?i)(https?|socks5)://[^\s/@]+:[^\s/@]+@", "$1://[REDACTED]@");
        return value.Replace('\r', ' ').Replace('\n', ' ');
    }
    public void Write(string message, bool error = false)
    {
        if (!error && settings.LogLevel is "WARNING" or "ERROR" or "CRITICAL")
            return;
        lock (sync)
            Console.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {(error ? "ERROR" : "INFO")} {Redact(message)}");
    }
    // Exception messages are never logged: they may contain response bodies or connection strings.
    public void Failure(string operation, Exception exception) => Write($"{operation}: {exception.GetType().Name}", true);
}
