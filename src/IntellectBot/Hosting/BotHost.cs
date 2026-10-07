using IntellectBot.Configuration;
using IntellectBot.Data;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;
using IntellectBot.Llm;
using IntellectBot.Telegram;
using Npgsql;

namespace IntellectBot.Hosting;

public static class BotHost
{
    public static async Task<int> RunAsync(AppSettings settings, CancellationToken ct)
    {
        var log = new SafeLog(settings);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        // Default HttpClient / hosting diagnostics could contain URLs with credentials.
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{settings.HealthPort}");
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));
        await using var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        var store = new PostgresStore(dataSource);
        using var telegramHttp = TelegramApi.CreateHttpClient(settings);
        using var llmHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var api = new TelegramApi(telegramHttp, settings.BotToken);
        var dialogue = new DialogueService(store, new LlmClient(llmHttp, settings.Llm, log), settings.Llm);
        var handler = new UpdateHandler(api, dialogue, log);
        var state = new RuntimeState();
        builder.Services.AddSingleton(api);
        builder.Services.AddSingleton(handler);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(log);
        builder.Services.AddHostedService<PollingService>();
        await using var app = builder.Build();
        app.MapGet("/health", async (HttpContext context) =>
        {
            var result = await HealthProbe.CheckAsync(state, store.PingAsync, context.RequestAborted);
            return Results.Json(new
            {
                status = result.Status,
                database = result.Database,
                polling = result.Polling
            }, statusCode: result.StatusCode);
        });
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            if (!await store.PingAsync(startup.Token))
                throw new IOException();
            log.Write("PostgreSQL подключён: SELECT 1 выполнен.");
            await store.InitializeAsync(startup.Token);
            log.Write("Таблицы настроек и истории готовы.");
            var me = await api.GetMeAsync(startup.Token);
            handler.BotUsername = me.Username;
            if (await api.HasWebhookAsync(startup.Token))
                throw new ConfigException("У бота установлен webhook. Удалите его перед запуском polling.");
            state.Initialized = true;
            log.Write($"Telegram доступен. Бот @{me.Username} запускает polling.");
            await app.RunAsync(ct);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
        catch (ConfigException error) { log.Write(error.Message, true); return 1; }
        catch (Exception error) { log.Failure("Запуск или работа приложения не выполнены. Проверьте конфигурацию и доступность сервисов", error); return 1; }
        finally { state.Initialized = false; }
    }
    public static async Task<int> HealthcheckAsync(CancellationToken ct, string envFile = ".env")
    {
        var values = DotEnv.Load(envFile);
        var port = values.TryGetValue("HEALTH_PORT", out var value) && value.Length > 0 ? value : "8080";
        if (!int.TryParse(port, out var number) || number is < 1 or > 65535)
            throw new ConfigException("HEALTH_PORT: неверный порт.");
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{number}/health", ct);
            using var body = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!response.IsSuccessStatusCode || body.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || !body.RootElement.TryGetProperty("status", out var status) || status.GetString() != "ok")
            {
                Console.WriteLine("Приложение не готово: проверьте процесс бота и PostgreSQL.");
                return 1;
            }
            Console.WriteLine("Приложение готово: polling работает, PostgreSQL отвечает на SELECT 1.\nДоступность Telegram при сетевых сбоях проверяйте по логам бота.");
            return 0;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Console.WriteLine("Проверка состояния недоступна. Убедитесь, что бот запущен.");
            return 1;
        }
    }
}
