using System.Text.Json;
using IntellectBot.Domain;
using IntellectBot.Mcp;

namespace IntellectBot.Agent;

internal sealed record AgentToolExecution(ToolAuditEntry Audit, string Action, string Reason,
    object ModelResult, string? UserError = null, PreparedReminder? Pending = null,
    JsonElement? SourceChoices = null, string? PublicText = null);

// Выполнение одного проверенного шага. Здесь нет генерации текста и управления циклом LLM.
internal sealed class AgentToolExecutor(IReadOnlyList<IToolProposalHandler> proposals)
{
    public async Task<AgentToolExecution> ExecuteAsync(AgentToolCall call,
        IReadOnlyList<McpToolDefinition> tools, IMcpGateway? mcp,
        long owner, long updateId, int proposalIndex, string? zone, CancellationToken ct)
    {
        var tool = tools.SingleOrDefault(value => value.Name == call.Name);
        if (tool is null)
            return Rejected("unknown_tool", "Аргументы не передавались.", "Неизвестный инструмент отклонён.",
                "Этот запрос содержит недоступное действие. Уточните, что нужно сделать.");
        JsonElement args;
        try
        {
            args = AgentInput.Validate(tool, call.Arguments);
        }
        catch (ReminderException error)
        {
            return Rejected(tool.Name, "Некорректные аргументы скрыты.",
                "Аргументы не соответствуют обнаруженной схеме.", error.Message);
        }
        if (!tool.ReadOnly)
        {
            var handler = proposals.SingleOrDefault(candidate => candidate.ToolName == tool.Name);
            if (handler is null)
                return Rejected(tool.Name, "Аргументы записи скрыты.",
                    "Для изменяющего метода не настроен сценарий подтверждения.",
                    "Этот инструмент изменяет данные. Его выполнение требует отдельного сценария подтверждения; действие не выполнено.");
            try
            {
                var pending = await handler.PrepareAsync(owner, updateId, proposalIndex, args, zone, ct);
                var draft = pending.Draft;
                return new(new(tool.Name,
                    ReminderDisplay.Summary(draft),
                    "passed", "pending_confirmation"), "call_tool",
                    "Напоминание подготовлено; запись ожидает отдельного подтверждения.",
                    new
                    {
                        success = true,
                        status = "pending_confirmation"
                    }, Pending: pending);
            }
            catch (ReminderException error) when (error.Code == "already_exists")
            {
                return new(new(tool.Name, "Совпадающие текст и время уже сохранены.", "passed", "already_exists"),
                    "call_tool", "Повторная запись одинакового напоминания не требуется.",
                    new
                    {
                        success = true,
                        status = "already_exists"
                    }, UserError: error.Message);
            }
            catch (ReminderException error)
            {
                return Rejected(tool.Name, "Параметры напоминания не приняты.",
                    "Нужно уточнить параметры напоминания.", error.Message);
            }
        }
        if (tool.Name == "get_schedule" && zone is null)
            return Rejected(tool.Name, "Дата принята; часовой пояс не задан.",
                "Для личного расписания требуется часовой пояс.",
                "Сначала выберите часовой пояс в настройках, затем повторите вопрос о расписании.");
        var outcome = await (mcp is IOwnerMcpGateway personal
            ? personal.CallForOwnerAsync(owner, tool.Name, args, ct)
            : mcp!.CallReadOnlyAsync(tool.Name, args, ct));
        var audit = new ToolAuditEntry(tool.Name, SafeArguments(args), "passed", outcome.Success && outcome.Data is not null ? "success" : "failed");
        if (!outcome.Success || outcome.Data is not { } data)
            return new(audit, "call_tool", "Инструмент недоступен или вернул ошибку.",
                new
                {
                    success = false,
                    error = outcome.ErrorCode ?? "missing_data",
                    message = "Данные не получены. Сообщи о недоступности и не выдумывай результат."
                }, PublicText: "Источник сейчас недоступен. Попробуйте позже; неподтверждённые данные не приводятся.");
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("status", out var sourceStatus) &&
            sourceStatus.ValueKind == JsonValueKind.String && sourceStatus.GetString() == "unavailable")
            audit = audit with
            {
                Execution = "failed"
            };
        var ambiguous = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("status", out var status) && status.GetString() == "ambiguous" &&
            data.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array;
        var publicText = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("public_text", out var display) && display.ValueKind == JsonValueKind.String &&
            display.GetString() is { Length: > 0 and <= 12000 } content ? content : null;
        return new(audit, "call_tool", tool.Name == "get_weather" ? "Нужны актуальные погодные данные." :
            "Нужны данные выбранного инструмента.", new
            {
                success = true,
                data
            },
            SourceChoices: ambiguous ? data : null, PublicText: publicText);
    }

    private static AgentToolExecution Rejected(string name, string arguments, string reason, string message) =>
        new(new(name, arguments, "rejected", "not_called"), "clarify", reason,
            new
            {
                success = false,
                error = "validation_failed"
            }, UserError: message);

    private static string SafeArguments(JsonElement arguments)
    {
        var summaries = arguments.EnumerateObject()
            .Where(property => !property.Name.EndsWith("id", StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Name switch
        {
            "city" when property.Value.ValueKind == JsonValueKind.String => "Город: " + property.Value.GetString(),
            "topic" when property.Value.ValueKind == JsonValueKind.String => "Тема: " + property.Value.GetString(),
            "date" when property.Value.ValueKind == JsonValueKind.String => "Дата: " + property.Value.GetString(),
            _ => property.Name + ": " + property.Value.ValueKind
        });
        var result = new string(string.Join("; ", summaries).Where(c => !char.IsControl(c)).ToArray());
        return result.Length == 0 ? "Параметры не требуются." : result[..Math.Min(result.Length, 480)];
    }
}
