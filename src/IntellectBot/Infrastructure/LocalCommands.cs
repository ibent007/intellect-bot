using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IntellectBot.Configuration;
using IntellectBot.Data;
using IntellectBot.Hosting;
using Npgsql;

namespace IntellectBot.Infrastructure;

public static class LocalCommands
{
    public static string Root => Directory.GetCurrentDirectory();
    public static void CheckRoot()
    {
        if (!File.Exists(Path.Combine(Root, "IntellectBot.sln")))
            throw new CommandException("Запустите команду из корня intellect_bot, где находится IntellectBot.sln.");
    }
    public static Dictionary<string, string> Values(AppSettings s) => new()
    {
        ["BOT_TOKEN"] = s.BotToken,
        ["TELEGRAM_PROXY_URL"] = s.TelegramProxyUrl,
        ["POSTGRES_HOST"] = s.PostgresHost,
        ["POSTGRES_PORT"] = s.PostgresPort.ToString(CultureInfo.InvariantCulture),
        ["POSTGRES_DB"] = s.PostgresDb,
        ["POSTGRES_USER"] = s.PostgresUser,
        ["POSTGRES_PASSWORD"] = s.PostgresPassword,
        ["LOG_LEVEL"] = s.LogLevel,
        ["HEALTH_PORT"] = s.HealthPort.ToString(CultureInfo.InvariantCulture),
        ["LLM_BASE_URL"] = s.Llm.BaseUrl,
        ["LLM_API_KEY"] = s.Llm.ApiKey,
        ["LLM_MODEL"] = s.Llm.Model,
        ["LLM_REASONING_EFFORT"] = s.Llm.ReasoningEffort,
        ["LLM_TIMEOUT_SECONDS"] = s.Llm.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        ["LLM_MAX_COMPLETION_TOKENS"] = s.Llm.MaxCompletionTokens.ToString(CultureInfo.InvariantCulture),
        ["LLM_HISTORY_MAX_MESSAGES"] = s.Llm.HistoryMaxMessages.ToString(CultureInfo.InvariantCulture),
        ["LLM_CONTEXT_MAX_CHARS"] = s.Llm.ContextMaxChars.ToString(CultureInfo.InvariantCulture),
        ["LLM_TOOL_RESPONSE_MAX_TOKENS"] = s.Llm.ToolResponseMaxTokens.ToString(CultureInfo.InvariantCulture),
        ["LLM_TOOL_CONTEXT_MAX_CHARS"] = s.Llm.ToolContextMaxChars.ToString(CultureInfo.InvariantCulture),
        ["LLM_MAX_RATE_WAIT_SECONDS"] = s.Llm.MaxRateWaitSeconds.ToString(CultureInfo.InvariantCulture)
    };
    private static string AskSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
            return Console.ReadLine()?.Trim() ?? "";
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString().Trim();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                    value.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
                value.Append(key.KeyChar);
        }
    }
    public static AppSettings Prepare(bool cloud)
    {
        CheckRoot();
        var path = cloud ? ".env.cloud" : ".env";
        var existing = File.Exists(path) ? DotEnv.Parse(File.ReadAllText(path)) : new Dictionary<string, string>();
        var effective = DotEnv.Load(path);
        var defaults = DotEnv.Parse(File.ReadAllText(".env.example"));
        foreach (var (key, value) in defaults)
            if (!effective.TryGetValue(key, out var current) || current.Length == 0)
                effective[key] = value;
        if (cloud)
        {
            effective["POSTGRES_HOST"] = "db";
            effective["POSTGRES_PORT"] = "5432";
            effective["HEALTH_PORT"] = "8080";
        }
        foreach (var key in new[] { "BOT_TOKEN", "LLM_API_KEY" })
            if (string.IsNullOrWhiteSpace(effective.GetValueOrDefault(key)))
                effective[key] = AskSecret($"{key} (ввод скрыт): ");
        if (string.IsNullOrEmpty(effective.GetValueOrDefault("POSTGRES_PASSWORD")))
            effective["POSTGRES_PASSWORD"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        if (cloud && string.IsNullOrWhiteSpace(effective.GetValueOrDefault("TELEGRAM_PROXY_URL")))
            effective["TELEGRAM_PROXY_URL"] = AskSecret("URL внешнего прокси Telegram (ввод скрыт): ");
        if (cloud && string.IsNullOrWhiteSpace(effective["TELEGRAM_PROXY_URL"]))
            throw new ConfigException("Для облака необходимо указать внешний прокси Telegram.");
        var settings = AppSettings.FromValues(effective);
        // Сохраняем только ключи конфигурации, без посторонних переменных среды.
        foreach (var (key, value) in Values(settings))
            existing[key] = value;
        DotEnv.PrivateWrite(path, "# Секретные настройки. Не добавляйте в Git.\n" + string.Concat(existing.Select(p => $"{p.Key}={DotEnv.Quote(p.Value)}\n")));
        return settings;
    }
    public static string[] ComposeArguments()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Root))))[..10].ToLowerInvariant();
        return ["compose", "--project-name", "intellect-csharp-" + hash, "--project-directory", Root, "--env-file", Path.Combine(Root, ".env"), "-f", Path.Combine(Root, "compose.yaml"), "-f", Path.Combine(Root, "compose.local.yaml")];
    }
    public static async Task<int> DatabaseCheckAsync(CancellationToken ct, string envFile = ".env")
    {
        await using var source = NpgsqlDataSource.Create(AppSettings.Load(envFile).ConnectionString);
        try
        {
            await new DatabaseProbe(source).PingAsync(ct);
            Console.WriteLine("PostgreSQL отвечает на SELECT 1.");
            return 0;
        }
        catch (Exception) { throw new CommandException("БД не прошла SELECT 1. Проверьте адрес и пароль: изменение .env не меняет пароль уже созданной роли PostgreSQL."); }
    }
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        CheckRoot();
        var action = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "up";
        if (action == "health")
            return await BotHost.HealthcheckAsync(ct);
        if (action is not ("up" or "stop" or "status" or "logs"))
            throw new CommandException("local: допустимы up, stop, status, logs, health.");
        await ProcessRunner.RunAsync("docker", ["info"], ct);
        await ProcessRunner.RunAsync("docker", ["compose", "version"], ct);
        var settings = action == "up" ? Prepare(false) : AppSettings.Load();
        if (settings.PostgresHost is not ("127.0.0.1" or "localhost"))
            throw new ConfigException("local запускает локальную БД. Для внешней БД используйте команду run.");
        var command = action switch
        {
            "up" => new[] { "up", "-d", "--wait", "--wait-timeout", "90", "db" },
            "stop" => ["stop", "db"],
            "status" => ["ps"],
            _ => ["logs", "--tail", "100", "db"]
        };
        var output = await ProcessRunner.RunAsync("docker", [.. ComposeArguments(), .. command], ct, environment: Values(settings));
        Console.WriteLine(new SafeLog(settings).Redact(output));
        if (action != "up")
            return 0;
        await DatabaseCheckAsync(ct);
        if (args.Contains("--setup-only"))
        {
            Console.WriteLine("Окружение готово. Запуск: dotnet run --project src/IntellectBot");
            return 0;
        }
        return await BotHost.RunAsync(settings, ct);
    }
}
