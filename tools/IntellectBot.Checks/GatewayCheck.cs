using System.Text.Json;
using IntellectBot.Configuration;
using IntellectBot.Mcp;

namespace IntellectBot.Checks;

public static class GatewayCheck
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var values = DotEnv.Load(".env");

            var serverPath =
                values.TryGetValue("MCP_SERVER_PATH", out var configuredPath) &&
                !string.IsNullOrWhiteSpace(configuredPath)
                    ? configuredPath
                    : "src/IntellectBot.McpServer/bin/Release/net10.0/" +
                      "IntellectBot.McpServer.dll";

            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(90));

            // Проверка клиента не подключается к БД и не создаёт напоминания.
            await using var gateway = await BotMcpClient.ConnectAsync(
                serverPath,
                databaseConnectionString: null,
                clock: TimeProvider.System,
                scheduleFile: values.GetValueOrDefault("Schedule__File"),
                scheduleTimeZone: values.GetValueOrDefault("Schedule__TimeZone"),
                cancellationToken: timeout.Token,
                demoReferenceTime: new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.FromHours(3)));

            var names = gateway.Catalog.Definitions
                .Select(tool => tool.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            if (new[] { "add_reminder", "get_reference", "get_schedule", "get_weather" }.Any(name => !names.Contains(name)) ||
                gateway.Catalog.Find("get_weather") is not { ReadOnly: true } ||
                gateway.Catalog.Find("get_reference") is not { ReadOnly: true } ||
                gateway.Catalog.Find("add_reminder") is not { ReadOnly: false })
                throw new InvalidOperationException();

            Console.WriteLine(
                $"Клиент: каталог ({names.Length} инструментов), схемы и права обнаружены — OK.");

            var arguments = JsonSerializer.SerializeToElement(new
            {
                date = "2026-10-12"
            });

            var schedule = await gateway.CallReadOnlyAsync(
                "get_schedule",
                arguments,
                timeout.Token);

            if (!schedule.Success ||
                schedule.Data is not { } data ||
                data.GetProperty("date").GetString() != "2026-10-12" ||
                data.GetProperty("lessons").ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException();
            }

            Console.WriteLine(
                "Клиент: расписание получено через отдельный процесс — OK.");

            var denied = await gateway.CallReadOnlyAsync(
                "add_reminder",
                JsonSerializer.SerializeToElement(new
                {
                    text = "Проверить код",
                    remind_at = "2099-01-01T18:00:00+03:00"
                }),
                timeout.Token);

            if (denied.Success || denied.ErrorCode != "tool_not_allowed")
                throw new InvalidOperationException();

            Console.WriteLine(
                "Клиент: прямой вызов записи заблокирован — OK.");

            try
            {
                await using var missing = await BotMcpClient.ConnectAsync(
                    Path.Combine(
                        Path.GetTempPath(),
                        Guid.NewGuid().ToString("N"),
                        "missing.dll"),
                    databaseConnectionString: null,
                    clock: TimeProvider.System,
                    cancellationToken: timeout.Token);

                throw new InvalidOperationException();
            }
            catch (McpUnavailableException)
            {
                Console.WriteLine(
                    "Клиент: отсутствие сервера обработано — OK.");
            }

            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine(
                "Проверка MCP-клиента не пройдена. " +
                "Проверьте сборку сервера, путь и источник расписания.");

            return 1;
        }
    }
}
