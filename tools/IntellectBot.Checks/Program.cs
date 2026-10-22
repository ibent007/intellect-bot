using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IntellectBot.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using IntellectBot.Checks;

try
{
    if (args.Length == 2 && args[0] == "--routing")
        return await RoutingExperiment.RunAsync(args[1]);
    if (args.Length == 4 && args[0] == "--routing" && args[2] == "--instructions")
        return await RoutingExperiment.RunAsync(args[1], args[3]);
    if (args.SequenceEqual(new[] { "--negative" }))
        return await NegativeCheck.RunAsync();
    if (args.SequenceEqual(new[] { "--decision-llm" }))
        return await DecisionCheck.RunAsync();
    if (args.SequenceEqual(new[] { "--sources" }))
        return await SourceCheck.RunAsync();
    if (args.SequenceEqual(new[] { "--gateway" }))
        return await GatewayCheck.RunAsync();

    if (args.Length > 1 ||
        (args.Length == 1 && args[0] != "--llm"))
    {
        Console.Error.WriteLine(
            "Запуск: IntellectBot.Checks [--llm | --gateway | --sources | --negative | --decision-llm | --routing ИМЯ [--instructions ФАЙЛ]]");
        return 1;
    }

    var serverPath = Path.GetFullPath(
        "src/IntellectBot.McpServer/bin/Release/net10.0/" +
        "IntellectBot.McpServer.dll");

    if (!File.Exists(serverPath))
        throw new InvalidOperationException(
            "Сначала выполните dotnet build -c Release.");

    // Не передаём серверу токены Telegram и LLM.
    var childEnvironment = new Dictionary<string, string?>();

    foreach (var name in new[]
    {
        "PATH", "HOME", "DOTNET_ROOT", "TMPDIR",
        "TEMP", "TMP", "SystemRoot"
    })
    {
        var value = Environment.GetEnvironmentVariable(name);

        if (value is not null)
            childEnvironment[name] = value;
    }

    // Автономный пример проверяется в фиксированную неделю независимо от сегодняшней даты.
    childEnvironment["Schedule__ReferenceTime"] = "2026-10-12T09:00:00+03:00";

    using var timeout = new CancellationTokenSource(
        TimeSpan.FromSeconds(90));

    var transport = new StdioClientTransport(
        new StdioClientTransportOptions
        {
            Name = "intellect-mcp",
            Command = "dotnet",
            Arguments = [serverPath],
            InheritEnvironmentVariables = false,
            EnvironmentVariables = childEnvironment
        });

    await using var client = await McpClient.CreateAsync(
        transport,
        cancellationToken: timeout.Token);

    var tools = await client.ListToolsAsync(
        cancellationToken: timeout.Token);

    if (new[] { "get_weather", "get_schedule", "add_reminder", "get_reference" }.Any(name => !tools.Any(tool => tool.Name == name)))
        throw new InvalidOperationException("Не обнаружены основные инструменты.");
    Console.WriteLine($"MCP: каталог ({tools.Count} инструментов) обнаружен — OK.");

    var scheduleTool = tools.Single(
        tool => tool.Name == "get_schedule");

    if (scheduleTool.ProtocolTool.OutputSchema is null)
        throw new InvalidOperationException(
            "Не обнаружена outputSchema расписания.");

    var scheduleResult = await client.CallToolAsync(
        scheduleTool.Name,
        new Dictionary<string, object?>
        {
            ["date"] = "2026-10-12"
        },
        cancellationToken: timeout.Token);

    if (scheduleResult.IsError == true ||
        scheduleResult.StructuredContent is not JsonElement scheduleJson)
    {
        throw new InvalidOperationException(
            "Расписание не вернуло structuredContent.");
    }

    if (scheduleJson.GetProperty("date").GetString() != "2026-10-12" ||
        scheduleJson.GetProperty("timezone").GetString() != "Europe/Moscow" ||
        scheduleJson.GetProperty("lessons").GetArrayLength() != 2 ||
        scheduleJson.GetProperty("lessons")[0]
            .GetProperty("title").GetString() != "Программирование")
    {
        throw new InvalidOperationException(
            "Получены неожиданные данные расписания.");
    }

    Console.WriteLine(
        "MCP: get_schedule, outputSchema и structuredContent — OK.");

    var resources = await client.ListResourcesAsync(
        cancellationToken: timeout.Token);

    if (!resources.Any(
        resource => resource.Uri == "schedule://current-week"))
    {
        throw new InvalidOperationException(
            "Ресурс недели не обнаружен.");
    }

    var weekResult = await client.ReadResourceAsync(
        "schedule://current-week",
        cancellationToken: timeout.Token);

    var weekText = weekResult.Contents
        .OfType<TextResourceContents>()
        .Single();

    using var weekDocument = JsonDocument.Parse(weekText.Text);

    if (weekText.MimeType != "application/json" ||
        weekDocument.RootElement.GetProperty("days").GetArrayLength() != 7)
    {
        throw new InvalidOperationException(
            "Ресурс недели вернул неожиданные данные.");
    }

    Console.WriteLine(
        "MCP: обнаружение и чтение schedule://current-week — OK.");

    var weatherTool = tools.Single(
        tool => tool.Name == "get_weather");

    if (weatherTool.ProtocolTool.OutputSchema is null ||
        !weatherTool.JsonSchema.GetProperty("properties")
            .TryGetProperty("city", out _))
    {
        throw new InvalidOperationException(
            "Не обнаружены схемы погодного инструмента.");
    }

    Console.WriteLine(
        "MCP: get_weather, inputSchema и outputSchema — OK.");

    var reminderTools = await client.ListToolsAsync();
    var reminderTool = reminderTools.Single(
        tool => tool.Name == "add_reminder");

    var reminderProperties = reminderTool.JsonSchema
        .GetProperty("properties")
        .EnumerateObject()
        .Select(property => property.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    if (!reminderProperties.SequenceEqual(
            new[] { "remind_at", "text" }) ||
        reminderTool.ProtocolTool.OutputSchema is null)
    {
        throw new InvalidOperationException(
            "Некорректная схема add_reminder.");
    }

    var reminderDenied = false;

    try
    {
        var deniedResult = await client.CallToolAsync(
            "add_reminder",
            new Dictionary<string, object?>
            {
                ["text"] = "Проверить код",
                ["remind_at"] = "2099-01-01T18:00:00+03:00"
            });

        reminderDenied = deniedResult.IsError == true;
    }
    catch (ModelContextProtocol.McpException)
    {
        reminderDenied = true;
    }

    if (!reminderDenied)
    {
        throw new InvalidOperationException(
            "add_reminder разрешён без доверенного контекста.");
    }

    Console.WriteLine(
        "MCP: add_reminder, схемы и запрет неподтверждённого вызова — OK.");

    if (args.Length == 0)
        return 0;

    var settings = AppSettings.Load().Llm;

    // Схема поступает от MCP-сервера, а не дублируется здесь.
    var payload = new Dictionary<string, object?>
    {
        ["model"] = settings.Model,
        ["messages"] = new[]
        {
            new
            {
                role = "user",
                content =
                    "Call get_schedule exactly once with date " +
                    "equal to 2026-10-12. Do not answer with ordinary text."
            }
        },
        ["tools"] = new[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = scheduleTool.Name,
                    description = scheduleTool.Description,
                    parameters = scheduleTool.JsonSchema
                }
            }
        },
        ["tool_choice"] = "auto",
        ["temperature"] = 0.0,
        ["max_completion_tokens"] = settings.MaxCompletionTokens,
        ["reasoning_effort"] = settings.ReasoningEffort
    };

    if (settings.Model.StartsWith(
        "qwen/", StringComparison.OrdinalIgnoreCase))
    {
        payload["reasoning_format"] = "hidden";
    }
    else
    {
        payload["include_reasoning"] = false;
    }

    using var handler = new HttpClientHandler
    {
        AllowAutoRedirect = false
    };

    using var http = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
    };

    using var request = new HttpRequestMessage(
        HttpMethod.Post,
        settings.BaseUrl.TrimEnd('/') + "/chat/completions");

    request.Headers.Authorization =
        new AuthenticationHeaderValue("Bearer", settings.ApiKey);

    request.Content = JsonContent.Create(payload);

    using var response = await http.SendAsync(
        request, timeout.Token);

    Console.WriteLine($"LLM: HTTP {(int)response.StatusCode}.");

    if (!response.IsSuccessStatusCode)
        return 1;

    using var document = JsonDocument.Parse(
        await response.Content.ReadAsStringAsync(timeout.Token));

    var choice = document.RootElement
        .GetProperty("choices")[0];

    if (choice.GetProperty("finish_reason").GetString() != "tool_calls")
        throw new InvalidOperationException(
            "Модель не вернула нативный вызов инструмента.");

    var calls = choice.GetProperty("message")
        .GetProperty("tool_calls");

    if (calls.GetArrayLength() != 1)
        throw new InvalidOperationException(
            "Ожидался ровно один вызов.");

    var call = calls[0];

    if (call.GetProperty("type").GetString() != "function" ||
        string.IsNullOrWhiteSpace(call.GetProperty("id").GetString()))
    {
        throw new InvalidOperationException(
            "Неверный формат вызова.");
    }

    var function = call.GetProperty("function");

    if (function.GetProperty("name").GetString() != scheduleTool.Name)
        throw new InvalidOperationException(
            "Модель выбрала неизвестный инструмент.");

    using var arguments = JsonDocument.Parse(
        function.GetProperty("arguments").GetString()
        ?? throw new InvalidOperationException(
            "Аргументы отсутствуют."));

    var input = arguments.RootElement;

    if (input.ValueKind != JsonValueKind.Object ||
        input.EnumerateObject().Count() != 1 ||
        !input.TryGetProperty("date", out var date) ||
        date.ValueKind != JsonValueKind.String ||
        date.GetString() != "2026-10-12")
    {
        throw new InvalidOperationException(
            "Аргументы инструмента не прошли проверку.");
    }

    var llmResult = await client.CallToolAsync(
        scheduleTool.Name,
        new Dictionary<string, object?>
        {
            ["date"] = date.GetString()
        },
        cancellationToken: timeout.Token);

    if (llmResult.IsError == true ||
        llmResult.StructuredContent is not { } llmSchedule ||
        llmSchedule.GetProperty("date").GetString() != "2026-10-12")
    {
        throw new InvalidOperationException(
            "Вызов, выбранный моделью, завершился ошибкой.");
    }

    Console.WriteLine(
        "LLM: нативный вызов, аргументы и выполнение через MCP — OK.");

    return 0;
}
catch (ConfigException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (Exception exception)
{
    // Без тела ответа API, ключей и конфигурации.
    Console.Error.WriteLine(
        $"Проверка не пройдена: {exception.GetType().Name}.");
    return 1;
}
