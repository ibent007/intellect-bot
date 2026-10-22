using System.Text.Json;
using IntellectBot.McpServer.Hosting;
using IntellectBot.McpServer.Weather;
using Microsoft.Extensions.Hosting;

if (args is ["--weather-check", var city])
{
    try
    {
        using var http = new HttpClient(
            new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        var result = await new WeatherService(http).GetAsync(city);

        Console.WriteLine(JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (WeatherException error)
    {
        Console.WriteLine($"Ошибка погоды: {error.Code}. {error.Message}");
        Environment.ExitCode = 1;
    }
    catch (Exception error)
    {
        Console.WriteLine($"Ошибка проверки: {error.GetType().Name}");
        Environment.ExitCode = 1;
    }

    return;
}

var builder = Host.CreateApplicationBuilder(args);
ServerComposition.Configure(builder);
await builder.Build().RunAsync();
