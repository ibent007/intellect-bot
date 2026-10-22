using System.ComponentModel;
using System.Text.Json;
using IntellectBot.Data;
using IntellectBot.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace IntellectBot.McpServer.Reference;

[McpServerToolType]
public sealed class ReferenceEndpoints(ReferenceService references, ToolRequestAuthorization authorization,
    TimeProvider clock, AgentStateStore? state = null)
{
    [McpServerTool(Name = "get_reference", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Проверяет энциклопедические факты по Википедии и данные о рождении по Wikidata. " +
        "Обязателен для биографий, возраста, основателей, дат и других конкретных фактов о людях и организациях. " +
        "Возвращает выдержку и ссылку на версию источника. Не дополняй её непроверенными сведениями.")]
    public async Task<ReferenceResult> GetReference(
        [Description("Полное имя человека или точное название темы; без вопросительных и вводных фраз.")] string topic,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken,
        [Description("Точный ID статьи из candidates при выборе пользователя; не придумывай ID.")] string? pageId = null,
        [Description("overview — справка; telegram — подтверждённая ссылка на Telegram, без биографии.")] string intent = "overview",
        [Description("person — вопрос о человеке или персонаже; any — остальные темы.")] string entityType = "any")
    {
        try
        {
            var now = clock.GetUtcNow();
            if (state is not null)
            {
                var request = context.Params;
                var owner = authorization.Verify(request.Meta, ToolInvocation.Operation(request.Name, JsonSerializer.SerializeToElement(request.Arguments)));
                var zone = await state.GetTimeZoneAsync(owner, cancellationToken);
                if (zone is not null)
                    now = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(zone));
            }
            return await references.GetAsync(topic, pageId, DateOnly.FromDateTime(now.DateTime), cancellationToken, intent, entityType);
        }
        catch (ArgumentException) { throw new McpException("Укажите полное имя или тему для проверки."); }
        catch (UnauthorizedAccessException) { throw new McpException("Не удалось проверить контекст запроса."); }
    }
}
