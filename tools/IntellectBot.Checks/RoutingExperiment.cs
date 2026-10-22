using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using IntellectBot.Agent;
using IntellectBot.Configuration;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;
using IntellectBot.Llm;
using IntellectBot.Mcp;

namespace IntellectBot.Checks;

public static class RoutingExperiment
{
    public static async Task<int> RunAsync(string version, string? instructionsPath = null)
    {
        if (version.Length is < 1 or > 40 ||
            version.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException(
                "Название запуска: латиница, цифры и дефис.");
        }

        var folder = Path.Combine("docs", "lab2-experiment");
        var destination = Path.Combine(folder, version);

        using var source = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(folder, "lab2-routing.json")));

        var context = source.RootElement.GetProperty("evaluation_context");
        var now = DateTimeOffset.Parse(
            context.GetProperty("now").GetString()!,
            CultureInfo.InvariantCulture);
        var zone = context.GetProperty("timezone").GetString()!;
        var cases = source.RootElement.GetProperty("cases")
            .EnumerateArray().ToArray();

        if (cases.Length != 12)
            throw new InvalidOperationException("Ожидаются 12 исходных запросов.");

        var settings = AppSettings.Load();
        const double temperature = 0.3;

        using var startup = new CancellationTokenSource(
            TimeSpan.FromSeconds(60));

        await using var mcp = await BotMcpClient.ConnectAsync(
            settings.McpServerPath,
            null,
            new FixedClock(now),
            settings.ScheduleFile,
            settings.ScheduleTimeZone,
            startup.Token, demoReferenceTime: now);

        using var http = new HttpClient(
            new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var protocolInstructions = instructionsPath is null
            ? AgentToolProtocol.Instructions : await File.ReadAllTextAsync(instructionsPath);
        if (string.IsNullOrWhiteSpace(protocolInstructions) || protocolInstructions.Length > 8000)
            throw new ArgumentException("Инструкция эксперимента: от 1 до 8000 символов.");
        var llm = new AgentLlmClient(http, settings.Llm, new SafeLog(settings),
            generateResponse: false, protocolInstructions: protocolInstructions);

        var rules = AgentTimeContext.Rules(now, zone);
        var systemPrompt = Prompts.For("chat") + "\n" + rules;
        var rows = new List<Row>();
        var resultsPath = Path.Combine(destination, "results.json");

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var configuration = JsonDefaults.Element(new
        {
            model = settings.Llm.Model,
            endpoint = settings.Llm.BaseUrl,
            temperature,
            settings.Llm.MaxCompletionTokens,
            settings.Llm.ToolResponseMaxTokens,
            settings.Llm.HistoryMaxMessages,
            settings.Llm.ContextMaxChars,
            settings.Llm.ToolContextMaxChars,
            settings.Llm.MaxRateWaitSeconds,
            settings.Llm.ReasoningEffort,
            now,
            timezone = zone,
            systemPrompt,
            protocolInstructions,
            agentAssemblySha256 = Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(typeof(AgentEngine).Assembly.Location))),
            nativeTools = AgentToolProtocol.Definitions(mcp.Catalog.Definitions, true),
            toolCatalog = mcp.Catalog.Definitions.OrderBy(tool => tool.Name, StringComparer.Ordinal),
            serverAssemblySha256 = Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(settings.McpServerPath))),
            dataset = source.RootElement
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(configuration.GetRawText())));
        var configurationPath = Path.Combine(destination, "configuration.json");
        if (File.Exists(configurationPath))
        {
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(configurationPath));
            if (!saved.RootElement.TryGetProperty("fingerprint", out var previous) || previous.GetString() != fingerprint)
                throw new InvalidOperationException("Условия запуска изменились. Используйте новое название запуска; смешивать результаты нельзя.");
        }
        else if (File.Exists(resultsPath))
            throw new InvalidOperationException("Нет сохранённых условий запуска. Используйте новое название.");
        if (File.Exists(resultsPath))
        {
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(resultsPath));
            rows = JsonSerializer.Deserialize<List<Row>>(saved.RootElement.GetProperty("rows").GetRawText()) ?? [];
            if (rows.Count > cases.Length || rows.Where((row, index) =>
                row.Id != cases[index].GetProperty("id").GetString() ||
                row.Query != cases[index].GetProperty("query").GetString() ||
                row.Expected != cases[index].GetProperty("expected_action").GetString()).Any())
                throw new InvalidOperationException("Сохранённые результаты не соответствуют набору запросов.");
            Console.WriteLine($"Продолжение: сохранено {rows.Count} из {cases.Length} запросов.");
        }
        Directory.CreateDirectory(destination);
        if (!File.Exists(configurationPath))
            await AtomicFile.WriteTextAsync(configurationPath,
                JsonSerializer.Serialize(new
                {
                    fingerprint,
                    configuration
                }, jsonOptions));

        foreach (var item in cases.Skip(rows.Count))
        {
            var id = item.GetProperty("id").GetString()!;
            var query = item.GetProperty("query").GetString()!;
            var expected = item.GetProperty("expected_action").GetString()!;

            try
            {
                using var deadline = new CancellationTokenSource(
                    TimeSpan.FromSeconds(120));

                // Каждый запрос получает пустую историю и одинаковое время.
                var messages = ContextBuilder.Build(
                        "chat",
                        Array.Empty<ChatMessage>(),
                        query,
                        settings.Llm.HistoryMaxMessages,
                        settings.Llm.ContextMaxChars -
                            ContextBuilder.Length(rules))
                    .Select(message =>
                        JsonSerializer.SerializeToElement(message))
                    .ToArray();

                messages[0] = JsonSerializer.SerializeToElement(new
                {
                    role = "system",
                    content = systemPrompt
                });

                var turn = await llm.CompleteAsync(
                    messages, mcp.Catalog.Definitions, temperature, true, deadline.Token);

                var call = turn.Calls.Count == 1 ? turn.Calls[0] : null;
                var actual = turn.Decision == "call_tool"
                    ? call?.Name ?? "invalid"
                    : turn.Decision;

                JsonElement? arguments = null;
                bool? correct = expected is "respond" or "clarify"
                    ? null
                    : false;
                var validation = "не требуется";

                if (call is not null)
                {
                    try
                    {
                        using var raw = JsonDocument.Parse(call.Arguments);
                        arguments = raw.RootElement.Clone();
                        var tool = mcp.Catalog.Definitions.Single(
                            tool => tool.Name == call.Name);
                        var validated = AgentInput.Validate(
                            tool, call.Arguments);

                        if (call.Name == "add_reminder")
                        {
                            var draft = ReminderValidation.Parse(
                                validated.GetProperty("text").GetString(),
                                validated.GetProperty("remind_at").GetString(),
                                zone);

                            if (draft.RemindAt <= now)
                                throw new InvalidOperationException();
                        }

                        validation = "пройдена";

                        if (correct is not null)
                        {
                            correct = actual == expected &&
                                Matches(validated,
                                    item.GetProperty("expected_arguments"));
                        }
                    }
                    catch (Exception error) when (
                        error is JsonException or ArgumentException or
                            InvalidOperationException or
                            ReminderException or LlmException)
                    {
                        validation = "не пройдена";
                    }
                }

                rows.Add(new Row(
                    id, query, expected, actual, arguments, correct,
                    actual == expected, validation, turn.Text));

                Console.WriteLine(
                    $"{id}: {actual}; аргументы: " +
                    $"{correct?.ToString() ?? "—"}");
            }
            catch (LlmException error)
            {
                await SaveAsync();
                Console.Error.WriteLine(
                    $"{id}: ошибка LLM {error.Code}. " +
                    "Запуск неполный; метрики не рассчитаны.");
                return 1;
            }
            catch (OperationCanceledException)
            {
                await SaveAsync();
                Console.Error.WriteLine(
                    $"{id}: время ожидания истекло. Запуск неполный.");
                return 1;
            }

            await SaveAsync();

            if (rows.Count < cases.Length)
                await Task.Delay(TimeSpan.FromSeconds(65));
        }

        return 0;

        async Task SaveAsync()
        {
            var complete = rows.Count == 12;
            var routing = rows.Count(row => row.RoutingCorrect);
            var arguments = rows.Count(
                row => row.ArgumentsCorrect == true);

            await AtomicFile.WriteTextAsync(
                Path.Combine(destination, "results.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        fingerprint,
                        complete,
                        routing_accuracy = complete ? (double?)routing / 12 : null,
                        argument_accuracy = complete ? (double?)arguments / 9 : null,
                        rows
                    }, jsonOptions));

            var lines = new List<string>
            {
                "| № | Запрос | Ожидаемое действие | Фактическое действие | Аргументы корректны | Результат |",
                "|---|---|---|---|---|---|"
            };

            lines.AddRange(rows.Select((row, index) =>
                $"| {index + 1} | {Cell(row.Query)} | " +
                $"{row.Expected} | {row.Actual} | " +
                $"{(row.ArgumentsCorrect is null ? "—" : row.ArgumentsCorrect.Value ? "да" : "нет")} | " +
                $"{(row.RoutingCorrect && row.ArgumentsCorrect != false ? "успех" : "ошибка")} |"));

            lines.Add(complete
                ? $"\nrouting_accuracy = {routing}/12 = {routing / 12.0:P2}" +
                  $"\n\nargument_accuracy = {arguments}/9 = {arguments / 9.0:P2}" +
                  $"\n\nПороги достигнуты: " +
                  $"{(routing >= 10 && arguments >= 8 ? "да" : "нет")}."
                : "\nЗапуск неполный. Итоговые метрики не рассчитаны.");

            await AtomicFile.WriteTextAsync(
                Path.Combine(destination, "results.md"), string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
    }

    private static string Cell(string value) => value
        .Replace("|", "\\|")
        .Replace("\r", " ")
        .Replace("\n", " ");

    private static bool Matches(
        JsonElement actual, JsonElement expected) =>
        expected.EnumerateObject().All(property =>
        {
            if (!actual.TryGetProperty(property.Name, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (property.Name == "remind_at")
            {
                return DateTimeOffset.TryParse(
                    value.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var time) &&
                    time == DateTimeOffset.Parse(
                        property.Value.GetString()!,
                        CultureInfo.InvariantCulture);
            }

            return string.Equals(
                value.GetString()?.Trim(),
                property.Value.GetString()?.Trim(),
                StringComparison.OrdinalIgnoreCase);
        });

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            now.ToUniversalTime();
    }

    private sealed record Row(
        string Id,
        string Query,
        string Expected,
        string Actual,
        JsonElement? Arguments,
        bool? ArgumentsCorrect,
        bool RoutingCorrect,
        string Validation,
        string? Response);
}
