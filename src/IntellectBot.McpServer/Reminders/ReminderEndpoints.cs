using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntellectBot.Domain;
using IntellectBot.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace IntellectBot.McpServer.Reminders;

public sealed record ReminderToolResult(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("remind_at")] string RemindAt,
    [property: JsonPropertyName("timezone")] string TimeZone,
    [property: JsonPropertyName("created")] bool Created);

[McpServerToolType]
public sealed class ReminderEndpoints(
    ReminderAuthorization authorization,
    IReminderExecutor executor)
{
    [McpServerTool(
        Name = "add_reminder",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Создаёт напоминание только после подтверждения пользователя. " +
        "Без доверенного контекста приложения выполнение запрещено.")]
    public Task<ReminderToolResult> AddReminder(
        [Description("Текст напоминания, от 1 до 500 символов.")]
        [MinLength(1), MaxLength(500)]
        string text,
        [Description(
            "Будущая дата ISO 8601 с явным смещением UTC, " +
            "например 2026-10-20T18:00:00+03:00.")]
        string remind_at,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        return ExecuteRequestAsync(context.Params, cancellationToken);
    }

    public async Task<ReminderToolResult> ExecuteRequestAsync(
        CallToolRequestParams request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var arguments = request.Arguments;

            // Владелец и ID подтверждения не могут быть аргументами модели.
            if (request.Name != "add_reminder" ||
                arguments is null ||
                arguments.Count != 2 ||
                !arguments.TryGetValue("text", out var textValue) ||
                !arguments.TryGetValue("remind_at", out var timeValue) ||
                textValue.ValueKind != JsonValueKind.String ||
                timeValue.ValueKind != JsonValueKind.String)
            {
                throw new ReminderException(
                    "invalid_arguments",
                    "Допустимы только строковые параметры text и remind_at.");
            }

            var authorized = authorization.Verify(
                request.Meta,
                textValue.GetString()!,
                timeValue.GetString()!);

            // Репозиторий повторно проверяет владельца, параметры,
            // подтверждение и срок действия в транзакции PostgreSQL.
            var result = await executor.ExecuteAsync(
                authorized, cancellationToken);

            var zone = TimeZoneInfo.FindSystemTimeZoneById(result.TimeZone);
            var localTime = TimeZoneInfo.ConvertTime(result.RemindAt, zone);

            return new ReminderToolResult(
                result.Id,
                localTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.ffffffzzz",
                    System.Globalization.CultureInfo.InvariantCulture),
                result.TimeZone,
                result.Created);
        }
        catch (ReminderException exception)
        {
            throw new McpException($"{exception.Code}: {exception.Message}");
        }
        catch (Exception exception) when (
            exception is NpgsqlException or TimeoutException)
        {
            throw new McpException(
                "unavailable: Создание напоминаний сейчас недоступно.");
        }
    }
}
