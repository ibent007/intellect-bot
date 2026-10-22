using System.Diagnostics;
using System.Text.Json;
using IntellectBot.Mcp;

namespace IntellectBot.Checks;

// Реальные публичные источники через отдельный MCP-процесс, без Telegram, LLM и рабочей БД.
public static class SourceCheck
{
    public static async Task<int> RunAsync()
    {
        var stage = "подключение MCP";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await using var gateway = await BotMcpClient.ConnectAsync(
                "src/IntellectBot.McpServer/bin/Release/net10.0/IntellectBot.McpServer.dll",
                databaseConnectionString: null, clock: TimeProvider.System,
                cancellationToken: timeout.Token,
                demoReferenceTime: new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.FromHours(3)));

            foreach (var city in new[] { "Дубай", "Питер", "Москва", "Самара", "Мичуринск" })
            {
                stage = "погода: " + city;
                var arguments = JsonSerializer.SerializeToElement(new
                {
                    city
                });
                var start = Stopwatch.GetTimestamp();
                var data = await Call("get_weather", arguments);
                if (data.GetProperty("status").GetString() == "ambiguous")
                {
                    var expectedCountry = city == "Дубай" ? "AE" : "RU";
                    var candidate = data.GetProperty("candidates").EnumerateArray().First(item =>
                        item.GetProperty("country_code").GetString() == expectedCountry);
                    arguments = candidate.GetProperty("selection_arguments").Clone();
                    data = await Call("get_weather", arguments);
                }
                Require(data, "weather");
                var cold = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                start = Stopwatch.GetTimestamp();
                Require(await Call("get_weather", arguments), "weather");
                Console.WriteLine($"Источник: {city}; ответ и кэш — OK; первый={cold:F0} мс, повтор={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} мс.");
            }
            stage = "справочный источник";
            var reference = await Call("get_reference", JsonSerializer.SerializeToElement(new
            {
                topic = "Людовико Эйнауди"
            }));
            Require(reference, "reference");
            if (!reference.GetProperty("url").GetString()!.StartsWith("https://ru.wikipedia.org/w/index.php?oldid=", StringComparison.Ordinal))
                throw new InvalidOperationException("Нет ссылки на версию статьи.");
            Console.WriteLine("Источник: справочная выдержка и постоянная ссылка — OK.");
            stage = "справка о человеке, а не фильме";
            var person = await Call("get_reference", JsonSerializer.SerializeToElement(new
            {
                topic = "Стив Джобс",
                entityType = "person"
            }));
            Require(person, "reference");
            if (person.GetProperty("title").GetString() != "Стив Джобс")
                throw new InvalidOperationException("Не выбран запрошенный человек.");
            Console.WriteLine("Источник: человек отделён от одноимённого фильма — OK.");
            stage = "ссылка на Telegram";
            var channel = await Call("get_reference", JsonSerializer.SerializeToElement(new
            {
                topic = "Павел Дуров",
                entityType = "person",
                intent = "telegram"
            }));
            Require(channel, "reference");
            if (!channel.GetProperty("public_text").GetString()!.Contains("https://t.me/", StringComparison.Ordinal))
                throw new InvalidOperationException("Источник не подтвердил Telegram-ссылку.");
            Console.WriteLine("Источник: Telegram-ссылка получена из Wikidata — OK.");
            return 0;

            async Task<JsonElement> Call(string tool, JsonElement arguments)
            {
                var result = await gateway.CallReadOnlyAsync(tool, arguments, timeout.Token);
                if (!result.Success || result.Data is not { } data)
                    throw new InvalidOperationException($"{tool}: {result.ErrorCode ?? "нет результата"}");
                return data;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Проверка публичных источников не пройдена (" + stage + "): " + error.GetType().Name + ". Проверьте доступность источника и повторите.");
            return 1;
        }
    }

    private static void Require(JsonElement data, string kind)
    {
        if (data.GetProperty("status").GetString() != "ok" || data.GetProperty("evidence_kind").GetString() != kind ||
            string.IsNullOrWhiteSpace(data.GetProperty("public_text").GetString()))
            throw new InvalidOperationException("Источник не вернул проверенное публичное представление.");
    }
}
