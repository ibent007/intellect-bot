using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using IntellectBot.McpServer.Schedule;
using Microsoft.Extensions.Configuration;
using IntellectBot.McpServer.Weather;
using IntellectBot.McpServer.Reference;
using IntellectBot.Data;
using IntellectBot.Mcp;
using IntellectBot.McpServer.Reminders;
using Npgsql;


namespace IntellectBot.McpServer.Hosting;

// Единственное место настройки источников данных и служб MCP-сервера
public static class ServerComposition
{
    public static void Configure(HostApplicationBuilder builder)
    {
        builder.Configuration
            .AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "mcp-settings.json"),
                optional: false,
                reloadOnChange: false)
            .AddEnvironmentVariables();

        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);


        builder.Services.AddSingleton(_ =>
            CreateHttpClient());

        static HttpClient CreateHttpClient()
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            })
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IntellectBot/1.0 (public reference lookup)");
            return client;
        }

        builder.Services.AddSingleton<ReferenceService>();
        builder.Services.AddSingleton<ReferenceEndpoints>();
        builder.Services.AddSingleton<WeatherService>();
        builder.Services.AddSingleton<WeatherEndpoints>();

        // stdout используется исключительно протоколом MCP
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly()
            .WithRequestFilters(filters => filters
                .AddListToolsFilter(ToolValidationFilter.Describe)
                .AddCallToolFilter(ToolValidationFilter.Wrap));

        var reminderKeyValue =
            Environment.GetEnvironmentVariable("MCP_REMINDER_KEY");

        var reminderDatabaseUrl =
            Environment.GetEnvironmentVariable("MCP_DATABASE_URL");

        byte[]? reminderKey = null;

        if (!string.IsNullOrWhiteSpace(reminderKeyValue))
        {
            try
            {
                reminderKey = Convert.FromBase64String(reminderKeyValue);

                if (reminderKey.Length != 32)
                    throw new FormatException();
            }
            catch (FormatException)
            {
                throw new InvalidOperationException(
                    "Некорректная конфигурация защиты MCP.");
            }
        }

        builder.Services.AddSingleton(serviceProvider =>
            new ReminderAuthorization(
                reminderKey,
                serviceProvider.GetRequiredService<TimeProvider>()));

        if (reminderKey is not null &&
            !string.IsNullOrWhiteSpace(reminderDatabaseUrl))
        {
            builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
                NpgsqlDataSource.Create(reminderDatabaseUrl));

            builder.Services.AddSingleton(serviceProvider =>
                new ReminderStore(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                    serviceProvider.GetRequiredService<TimeProvider>()));

            builder.Services.AddSingleton<IReminderExecutor>(serviceProvider =>
                new ReminderExecutor(
                    serviceProvider.GetRequiredService<ReminderStore>()));
        }
        else
        {
            builder.Services.AddSingleton<IReminderExecutor>(
                new ReminderExecutor(null));
        }

        builder.Services.AddSingleton(serviceProvider => new ToolRequestAuthorization(reminderKey,
            serviceProvider.GetRequiredService<TimeProvider>()));
        if (reminderKey is not null && !string.IsNullOrWhiteSpace(reminderDatabaseUrl))
        {
            builder.Services.AddSingleton<PersonalScheduleStore>();
            builder.Services.AddSingleton<AgentStateStore>();
            builder.Services.AddSingleton<PersonalScheduleReader>();
            builder.Services.AddSingleton(serviceProvider => new ScheduleEndpoints(
                serviceProvider.GetRequiredService<ToolRequestAuthorization>(),
                serviceProvider.GetRequiredService<PersonalScheduleReader>()));
        }
        else
        {
            // Обезличенный пример доступен только при автономной проверке без подключения БД
            var path = Path.GetFullPath(builder.Configuration["Schedule:File"] ?? "data/demo-schedule.json", AppContext.BaseDirectory);
            var zone = builder.Configuration["Schedule:TimeZone"] ?? "Europe/Moscow";
            builder.Services.AddSingleton(serviceProvider => new DemoScheduleSource(File.ReadAllText(path), zone,
                DemoScheduleClock.FromValue(builder.Configuration["Schedule:ReferenceTime"],
                    serviceProvider.GetRequiredService<TimeProvider>())));
            builder.Services.AddSingleton<ScheduleEndpoints>();
        }

        builder.Services.AddSingleton<ReminderEndpoints>();

    }
}
