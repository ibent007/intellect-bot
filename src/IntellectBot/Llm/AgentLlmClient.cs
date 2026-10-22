using System.Text.Json;
using IntellectBot.Agent;
using IntellectBot.Configuration;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;
using IntellectBot.Mcp;

namespace IntellectBot.Llm;

// Один шаг цикла. Повтор исправляет только ответ модели до исполнения инструментов.
public sealed class AgentLlmClient(HttpClient http, LlmSettings settings, SafeLog log,
    bool generateResponse = true, string? protocolInstructions = null) : IAgentLlmClient
{
    private readonly ChatCompletionTransport transport = new(http, settings, log);

    public async Task<AgentModelTurn> CompleteAsync(IReadOnlyList<JsonElement> messages,
        IReadOnlyList<McpToolDefinition> tools, double temperature, bool allowTools,
        CancellationToken ct, string? model = null)
    {
        if (!Modes.Temperatures.Contains(temperature))
            throw new LlmException("invalid_temperature");
        var selected = LlmModels.Resolve(model, settings.Model);
        var instructions = protocolInstructions ?? AgentToolProtocol.Instructions;
        if (tools.Count == 0)
            instructions = "Инструменты недоступны. Отвечай текстом; не выдумывай актуальные данные.";
        else
            instructions += $" Служебная функция уточнения: {AgentToolProtocol.ClarificationName(tools)}." +
                (allowTools ? "" : " Лимит MCP-действий исчерпан. Ответь по результатам или уточни запрос.");
        var wire = new List<JsonElement> { JsonDefaults.Element(new { role = "system", content = instructions }) };
        wire.AddRange(messages);
        if (generateResponse && ToolReplyWindow.HasResult(messages))
            wire = ToolReplyWindow.Build(messages);
        var hasCurrentResult = messages.Reverse().TakeWhile(message => message.GetProperty("role").GetString() != "user")
            .Any(message => message.GetProperty("role").GetString() == "tool");
        var lastUser = messages.LastOrDefault(message => message.GetProperty("role").GetString() == "user");
        var questionText = lastUser.ValueKind == JsonValueKind.Object ? lastUser.GetProperty("content").GetString() ?? "" : "";
        var translation = generateResponse && TranslationReply.IsRequest(questionText);
        if (translation)
        {
            wire.Add(JsonDefaults.Element(new
            {
                role = "system",
                content = TranslationReply.Instructions(questionText)
            }));
        }
        var compactStep = hasCurrentResult || generateResponse &&
            (AgentAnswerPolicy.RequiredEvidence(questionText) is not null || ReminderRequestGuard.IsRequest(questionText));
        var payload = new Dictionary<string, object?>
        {
            ["model"] = selected,
            ["messages"] = wire,
            ["temperature"] = translation ? 0.0 : temperature,
            ["max_completion_tokens"] = compactStep ? Math.Min(settings.MaxCompletionTokens, settings.ToolResponseMaxTokens) : settings.MaxCompletionTokens
        };
        if (tools.Count > 0 && !translation)
        {
            payload["tools"] = AgentToolProtocol.Definitions(tools, allowTools);
            payload["tool_choice"] = "auto";
            payload["parallel_tool_calls"] = false;
        }
        ModelCapabilities.Apply(payload, selected, settings.ReasoningEffort);
        for (var attempt = 0; ; attempt++)
        {
            string? requestId = null;
            try
            {
                var reply = await transport.SendAsync(payload, ct);
                requestId = reply.RequestId;
                var turn = AgentToolProtocol.Interpret(Parse(reply.Data), tools, allowTools);
                var question = messages.LastOrDefault(message => message.GetProperty("role").GetString() == "user");
                if (generateResponse && turn.Calls.Count == 0 && turn.Text is { } text && !ToolResultContext.PublicTexts(messages).Any())
                {
                    if (question.ValueKind == JsonValueKind.Object && AgentAnswerPolicy.IsEcho(question.GetProperty("content").GetString()!, text))
                        throw new LlmException("unhelpful_response");
                    if (AgentAnswerPolicy.HasMixedScriptWords(text))
                        throw new LlmException("invalid_language");
                    if (turn.Decision == "clarify" && (text.Length < 12 || !text.Contains('?')))
                        throw new LlmException("invalid_clarification");
                }
                if (generateResponse && turn.Calls.Count == 0 && turn.Decision == "respond" && translation &&
                    (!TranslationReply.MatchesRequest(turn.Text ?? "", questionText) || !questionText.Contains('→') &&
                    (turn.Text ?? "").Count(c => c == '→') > 1))
                {
                    throw new LlmException("invalid_translation");
                }
                return turn;
            }
            catch (LlmException error)
            {
                log.Write($"Ответ LLM отклонён: request_id={requestId ?? "http_rejected"} model={selected} code={error.Code}", true);
                var retry = generateResponse && attempt == 0 && (error.ApiCode == "tool_use_failed" ||
                    error.Code is "unhelpful_response" or "invalid_language" or "invalid_clarification" or "empty_response" or "invalid_translation");
                if (!retry)
                    throw;
                wire.Add(JsonDefaults.Element(new
                {
                    role = "system",
                    content =
                    "Предыдущий ответ не принят. Сформируй один корректный ответ заново. " +
                    "Уточнение — полный вопрос через служебную функцию; инструмент — только объявленное имя и аргументы по его схеме. " +
                    "Не используй обёртки action/name/arguments. Пиши по-русски без смешения алфавитов внутри слов, " +
                    "английские имена и код сохраняй отдельно. Не повторяй обрывок запроса вместо ответа. " +
                    "Для перевода: конкретные названия языков по-русски, например Русский → Немецкий, пустая строка и только перевод. Без исходника и без слов Язык оригинала."
                }));
                log.Write($"Исправление ответа LLM: model={selected} reason={error.Code}");
            }
        }
    }
    public static AgentModelTurn Parse(JsonElement data) => CompletionParser.Agent(data);
}
