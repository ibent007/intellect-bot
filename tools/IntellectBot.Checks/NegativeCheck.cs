using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace IntellectBot.Checks;

public static class NegativeCheck
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var path = Path.GetFullPath("src/IntellectBot.McpServer/bin/Release/net10.0/IntellectBot.McpServer.dll");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var name in new[] { "DOTNET_ROOT", "TMPDIR", "TEMP", "TMP" })
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    environment[name] = value;
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
            {
                Command = "dotnet",
                Arguments = [path],
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                StandardErrorLines = _ => { }
            }), cancellationToken: timeout.Token);
            foreach (var entry in new (string Name, Dictionary<string, object?> Args)[]
            {
                ("unknown_tool", new()),
                ("get_schedule", new() { ["date"] = "12.10.2026" }),
                ("get_schedule", new() { ["date"] = "2026-10-12", ["owner"] = 42 }),
                ("get_weather", new() { ["city"] = new string('x', 101) }),
                ("add_reminder", new() { ["text"] = "Задание", ["remind_at"] = "2030-01-15T18:00:00+03:00" })
            })
            {
                var rejected = false;
                try
                {
                    var result = await client.CallToolAsync(entry.Name, entry.Args, cancellationToken: timeout.Token);
                    rejected = result.IsError == true;
                }
                catch (McpException) { rejected = true; }
                if (!rejected)
                    throw new InvalidOperationException();
                Console.WriteLine($"MCP: отклонение {entry.Name} — OK.");
            }
            return 0;
        }
        catch (Exception) { Console.Error.WriteLine("Негативная проверка MCP не пройдена."); return 1; }
    }
}
