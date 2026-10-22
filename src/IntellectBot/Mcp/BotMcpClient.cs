using System.Text.Json;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IntellectBot.Mcp;

public sealed class BotMcpClient : IConfirmedReminderGateway, IOwnerMcpGateway, IAsyncDisposable
{
    private const string ScheduleResource = "schedule://current-week";

    private readonly McpClient client;
    private readonly ReminderAuthorization authorization;
    private readonly ToolRequestAuthorization requestAuthorization;

    public McpToolCatalog Catalog
    {
        get;
    }

    private BotMcpClient(
        McpClient client,
        ReminderAuthorization authorization,
        ToolRequestAuthorization requestAuthorization,
        IReadOnlyList<McpToolDefinition> tools)
    {
        this.client = client;
        this.authorization = authorization;
        this.requestAuthorization = requestAuthorization;
        Catalog = new McpToolCatalog(tools);
    }

    public static async Task<BotMcpClient> ConnectAsync(
        string serverDll,
        string? databaseConnectionString,
        TimeProvider clock,
        string? scheduleFile = null,
        string? scheduleTimeZone = null,
        CancellationToken cancellationToken = default,
        DateTimeOffset? demoReferenceTime = null)
    {
        McpClient? connectedClient = null;

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var fullPath = Path.GetFullPath(serverDll);

            if (!File.Exists(fullPath))
                throw new McpUnavailableException();

            var key = System.Security.Cryptography.RandomNumberGenerator
                .GetBytes(32);

            var environment =
                StdioClientTransportOptions.GetDefaultEnvironmentVariables();

            foreach (var name in new[]
            {
                "DOTNET_ROOT",
                "DOTNET_ROOT_ARM64",
                "DOTNET_ROOT_X64",
                "TMPDIR",
                "TMP",
                "TEMP",
                "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
                "http_proxy", "https_proxy", "all_proxy", "no_proxy"
            })
            {
                var value = Environment.GetEnvironmentVariable(name);

                if (!string.IsNullOrEmpty(value))
                    environment[name] = value;
            }

            // Передаём только настройки запуска и стандартные сетевые прокси источников.
            // Их значения не включаются в сообщения модели или журнал.
            // Только служебные данные, необходимые нашему MCP-серверу.
            // BOT_TOKEN, LLM_API_KEY и Telegram-прокси не передаются.
            environment["MCP_REMINDER_KEY"] = Convert.ToBase64String(key);

            if (!string.IsNullOrWhiteSpace(databaseConnectionString))
                environment["MCP_DATABASE_URL"] = databaseConnectionString;

            if (!string.IsNullOrWhiteSpace(scheduleFile))
                environment["Schedule__File"] = Path.GetFullPath(scheduleFile);

            if (!string.IsNullOrWhiteSpace(scheduleTimeZone))
                environment["Schedule__TimeZone"] = scheduleTimeZone;

            if (string.IsNullOrWhiteSpace(databaseConnectionString) && demoReferenceTime is { } demoNow)
                environment["Schedule__ReferenceTime"] = demoNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

            var transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = "intellect-mcp",
                    Command = "dotnet",
                    Arguments = [fullPath],
                    WorkingDirectory = Path.GetDirectoryName(fullPath),
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = environment,
                    ShutdownTimeout = TimeSpan.FromSeconds(5),

                    // Не переносим необработанный stderr в журнал приложения.
                    StandardErrorLines = _ => { }
                });

            connectedClient = await McpClient.CreateAsync(
                transport,
                cancellationToken: timeout.Token);

            var discovered = await connectedClient.ListToolsAsync(
                cancellationToken: timeout.Token);

            var definitions = new List<McpToolDefinition>();

            foreach (var tool in discovered)
            {
                var protocol = JsonSerializer.SerializeToElement(tool.ProtocolTool);
                var readOnly = protocol.TryGetProperty("annotations", out var annotations) &&
                    annotations.TryGetProperty("readOnlyHint", out var hint) && hint.ValueKind == JsonValueKind.True;
                // Отсутствующая аннотация не даёт разрешения на автоматическую запись.
                definitions.Add(new McpToolDefinition(tool.Name, tool.Description ?? "",
                    tool.JsonSchema.Clone(),
                    tool.ProtocolTool.OutputSchema?.Clone() ?? JsonSerializer.SerializeToElement(true), readOnly));
            }

            return new BotMcpClient(
                connectedClient,
                new ReminderAuthorization(key, clock),
                new ToolRequestAuthorization(key, clock),
                definitions.AsReadOnly());
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (connectedClient is not null)
                await connectedClient.DisposeAsync();

            throw new McpUnavailableException();
        }
        catch
        {
            if (connectedClient is not null)
                await connectedClient.DisposeAsync();

            throw;
        }
    }

    public Task<McpToolOutcome> CallForOwnerAsync(long owner, string toolName, JsonElement arguments, CancellationToken ct)
    {
        if (Catalog.Find(toolName) is not { ReadOnly: true })
            return Task.FromResult(new McpToolOutcome(false, null, "tool_not_allowed"));
        if (!Catalog.AcceptsArguments(toolName, arguments))
            return Task.FromResult(new McpToolOutcome(false, null, "invalid_arguments"));
        return CallAsync(new CallToolRequestParams
        {
            Name = toolName,
            Arguments = ToArguments(arguments),
            Meta = requestAuthorization.Create(owner, ToolInvocation.Operation(toolName, arguments))
        }, ct);
    }

    // Агент сможет напрямую выполнять только инструменты чтения.
    public Task<McpToolOutcome> CallReadOnlyAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (Catalog.Find(toolName) is not { ReadOnly: true })
        {
            return Task.FromResult(new McpToolOutcome(
                false, null, "tool_not_allowed"));
        }

        if (!Catalog.AcceptsArguments(toolName, arguments))
        {
            return Task.FromResult(new McpToolOutcome(
                false, null, "invalid_arguments"));
        }

        var request = new CallToolRequestParams
        {
            Name = toolName,
            Arguments = ToArguments(arguments)
        };

        return CallAsync(request, cancellationToken);
    }

    // Вызывается доверенным приложением после подтверждения пользователя.
    // Модель не получает доступа к этому методу и его служебным параметрам.
    public Task<McpToolOutcome> ExecuteConfirmedReminderAsync(
        long ownerChatId,
        PreparedReminder action,
        CancellationToken cancellationToken = default)
    {
        var metadata = authorization.CreateMetadata(ownerChatId, action);

        var request = new CallToolRequestParams
        {
            Name = "add_reminder",
            Meta = metadata,
            Arguments = new Dictionary<string, JsonElement>
            {
                ["text"] = JsonSerializer.SerializeToElement(action.Draft.Text),
                ["remind_at"] = JsonSerializer.SerializeToElement(
                    ReminderAuthorization.FormatTime(action.Draft.RemindAt))
            }
        };

        return CallAsync(request, cancellationToken);
    }

    public Task<McpToolOutcome> ReadCurrentWeekAsync(CancellationToken cancellationToken = default) =>
        ReadWeekAsync(null, cancellationToken);

    public Task<McpToolOutcome> ReadCurrentWeekForOwnerAsync(long owner, CancellationToken ct = default) => ReadWeekAsync(owner, ct);

    private async Task<McpToolOutcome> ReadWeekAsync(long? owner, CancellationToken cancellationToken)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            var result = await client.ReadResourceAsync(
                new ReadResourceRequestParams
                {
                    Uri = ScheduleResource,
                    Meta = owner is null ? null : requestAuthorization.Create(owner.Value, "week")
                },
                cancellationToken: timeout.Token);

            var contents = result.Contents
                .OfType<TextResourceContents>()
                .ToArray();

            if (contents.Length != 1 ||
                contents[0].Text.Length > 20_000)
            {
                return new McpToolOutcome(
                    false, null, "invalid_response");
            }

            using var document = JsonDocument.Parse(contents[0].Text);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new McpToolOutcome(
                    false, null, "invalid_response");
            }

            return new McpToolOutcome(
                true, document.RootElement.Clone(), null);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new McpToolOutcome(
                false, null, "resource_unavailable");
        }
    }

    private async Task<McpToolOutcome> CallAsync(
        CallToolRequestParams request,
        CancellationToken cancellationToken)
    {
        var arguments = JsonSerializer.SerializeToElement(request.Arguments);
        if (!Catalog.AcceptsArguments(request.Name, arguments))
            return new(false, null, "invalid_arguments");

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Погода включает два последовательных HTTP-запроса сервера.
        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        try
        {
            var result = await client.CallToolAsync(
                request,
                cancellationToken: timeout.Token);

            if (result.IsError == true)
            {
                return new McpToolOutcome(false, null, "tool_error");
            }

            JsonElement data;
            if (result.StructuredContent is { } structured)
                data = structured;
            else if (Catalog.Find(request.Name)!.OutputSchema.ValueKind == JsonValueKind.True)
            {
                // MCP допускает текстовый результат, если outputSchema не объявлена.
                var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
                if (string.IsNullOrWhiteSpace(text))
                    return new(false, null, "unsupported_content");
                data = JsonSerializer.SerializeToElement(new
                {
                    text
                });
            }
            else
                return new(false, null, "invalid_response");
            if (JsonDefaults.Text(data).Length > 20_000 ||
                !Catalog.AcceptsResult(request.Name, data))
            {
                return new McpToolOutcome(
                    false, null, "invalid_response");
            }

            return new McpToolOutcome(true, data.Clone(), null);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Не возвращаем модели исключения, строки подключения и stderr.
            return new McpToolOutcome(
                false, null, "tool_unavailable");
        }
    }

    private static Dictionary<string, JsonElement> ToArguments(
        JsonElement arguments)
    {
        var result = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);

        foreach (var property in arguments.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value.Clone()))
                throw new ArgumentException("Повторяющийся параметр.");
        }

        return result;
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
