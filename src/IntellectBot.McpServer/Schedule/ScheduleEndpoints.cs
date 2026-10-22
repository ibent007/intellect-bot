using System.ComponentModel;
using System.Text.Json;
using IntellectBot.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;
namespace IntellectBot.McpServer.Schedule;

// Граница MCP: проверяет доверенный контекст, затем вызывает источник расписания.
[McpServerToolType]
[McpServerResourceType]
public sealed class ScheduleEndpoints(ToolRequestAuthorization authorization, PersonalScheduleReader? personal = null, DemoScheduleSource? demo = null)
{
    [McpServerTool(Name = "get_schedule", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Показывает личное расписание на указанную дату. Дата — ГГГГ-ММ-ДД. Пустой список означает, что пользователь не добавил занятий на этот день.")]
    public async Task<DaySchedule> GetSchedule(
        [Description("Точная дата в формате ГГГГ-ММ-ДД, например 2030-01-15.")] string date,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        return await ExecuteRequestAsync(context.Params, cancellationToken);
    }

    public async Task<DaySchedule> ExecuteRequestAsync(CallToolRequestParams request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.Name != "get_schedule" || request.Arguments?.Count != 1 ||
                !request.Arguments.TryGetValue("date", out var argument) || argument.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Допустима только строковая дата.");
            var date = argument.GetString()!;
            if (personal is not null)
            {
                var owner = authorization.Verify(request.Meta, ToolInvocation.Operation(request.Name, JsonSerializer.SerializeToElement(request.Arguments)));
                return await personal.GetDayAsync(owner, date, cancellationToken);
            }
            return (demo ?? throw new ScheduleUnavailableException("Расписание недоступно.")).GetDay(date);
        }
        catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or ScheduleUnavailableException)
        {
            throw new McpException(e.Message);
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException)
        {
            throw new McpException("Не удалось прочитать расписание. Попробуйте позже.");
        }
    }

    [McpServerResource(UriTemplate = "schedule://current-week", Name = "current_week_schedule", MimeType = "application/json")]
    [Description("Личное расписание текущей недели, с понедельника по воскресенье, в часовом поясе пользователя.")]
    public async Task<string> GetCurrentWeek(RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        try
        {
            var week = personal is null ? (demo ?? throw new ScheduleUnavailableException("Расписание недоступно.")).GetCurrentWeek() :
                await personal.GetWeekAsync(authorization.Verify(context.Params.Meta, "week"), cancellationToken);
            return JsonSerializer.Serialize(week);
        }
        catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or ScheduleUnavailableException)
        {
            throw new McpException(e.Message);
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException)
        {
            throw new McpException("Не удалось прочитать расписание. Попробуйте позже.");
        }
    }
}
