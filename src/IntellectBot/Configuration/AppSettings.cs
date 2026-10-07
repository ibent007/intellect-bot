using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;

namespace IntellectBot.Configuration;

public sealed class ConfigException(string message) : Exception(message);

// Classes intentionally do not synthesize ToString(): configuration contains secrets.
public sealed class AppSettings
{
    public required string BotToken
    {
        get; init;
    }
    public required string PostgresPassword
    {
        get; init;
    }
    public string PostgresHost { get; init; } = "127.0.0.1";
    public int PostgresPort { get; init; } = 5432;
    public string PostgresDb { get; init; } = "bot";
    public string PostgresUser { get; init; } = "bot";
    public string TelegramProxyUrl { get; init; } = "";
    public string LogLevel { get; init; } = "INFO";
    public int HealthPort { get; init; } = 8080;
    public required LlmSettings Llm
    {
        get; init;
    }
    public string ConnectionString => new NpgsqlConnectionStringBuilder
    {
        Host = PostgresHost,
        Port = PostgresPort,
        Database = PostgresDb,
        Username = PostgresUser,
        Password = PostgresPassword,
        Timeout = 5,
        CommandTimeout = 15,
        MaxPoolSize = 10,
        IncludeErrorDetail = false,
        ApplicationName = "intellect-bot"
    }.ConnectionString;

    public static AppSettings Load(string path = ".env") => FromValues(DotEnv.Load(path));
    public static AppSettings FromValues(IReadOnlyDictionary<string, string> values)
    {
        string Get(string key, string fallback = "") => values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
        string Required(string key) => Get(key).Trim() is { Length: > 0 } v ? v : throw new ConfigException($"Не задан {key}.");
        int Number(string key, int fallback, int max = int.MaxValue) => int.TryParse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 && n <= max ? n : throw new ConfigException($"{key}: требуется целое число от 1 до {max}.");
        var token = Required("BOT_TOKEN");
        if (!Regex.IsMatch(token, @"^\d+:[^\s:]+$"))
            throw new ConfigException("BOT_TOKEN: неверный формат токена.");
        var proxy = Get("TELEGRAM_PROXY_URL");
        if (proxy.Length > 0 && (!Uri.TryCreate(proxy, UriKind.Absolute, out var p) || p.Scheme is not ("http" or "socks5") || string.IsNullOrEmpty(p.Host) || p.Port <= 0 || p.AbsolutePath is not ("" or "/") || p.Query.Length > 0 || p.Fragment.Length > 0 || !Regex.IsMatch(proxy, @"^\w+://[^/?#]+:\d+/?$")))
            throw new ConfigException("TELEGRAM_PROXY_URL: укажите http://host:port или socks5://host:port.");
        var level = Get("LOG_LEVEL", "INFO").ToUpperInvariant();
        if (!new[] { "DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL" }.Contains(level))
            throw new ConfigException("LOG_LEVEL: недопустимый уровень.");
        var url = Required("LLM_BASE_URL").TrimEnd('/');
        if (url.Any(char.IsWhiteSpace) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || string.IsNullOrEmpty(uri.Host))
            throw new ConfigException("LLM_BASE_URL: требуется HTTPS-адрес без учётных данных, параметров и фрагмента.");
        var effort = Get("LLM_REASONING_EFFORT", "low");
        if (effort is not ("low" or "medium" or "high"))
            throw new ConfigException("LLM_REASONING_EFFORT: допустимы low, medium, high.");
        return new AppSettings
        {
            BotToken = token,
            PostgresPassword = Get("POSTGRES_PASSWORD") is { Length: > 0 } password ? password : throw new ConfigException("POSTGRES_PASSWORD: пароль базы данных не задан."),
            PostgresHost = Get("POSTGRES_HOST", "127.0.0.1"),
            PostgresPort = Number("POSTGRES_PORT", 5432, 65535),
            PostgresDb = Get("POSTGRES_DB", "bot"),
            PostgresUser = Get("POSTGRES_USER", "bot"),
            TelegramProxyUrl = proxy,
            LogLevel = level,
            HealthPort = Number("HEALTH_PORT", 8080, 65535),
            Llm = new LlmSettings
            {
                BaseUrl = url,
                ApiKey = Required("LLM_API_KEY"),
                Model = Required("LLM_MODEL"),
                ReasoningEffort = effort,
                TimeoutSeconds = Number("LLM_TIMEOUT_SECONDS", 60),
                MaxCompletionTokens = Number("LLM_MAX_COMPLETION_TOKENS", 2048),
                HistoryMaxMessages = Number("LLM_HISTORY_MAX_MESSAGES", 12),
                ContextMaxChars = Number("LLM_CONTEXT_MAX_CHARS", 6000)
            }
        };
    }
}
public sealed class LlmSettings
{
    public required string BaseUrl
    {
        get; init;
    }
    public required string ApiKey
    {
        get; init;
    }
    public required string Model
    {
        get; init;
    }
    public string ReasoningEffort { get; init; } = "low";
    public int TimeoutSeconds { get; init; } = 60;
    public int MaxCompletionTokens { get; init; } = 2048;
    public int HistoryMaxMessages { get; init; } = 12;
    public int ContextMaxChars { get; init; } = 6000;
}
