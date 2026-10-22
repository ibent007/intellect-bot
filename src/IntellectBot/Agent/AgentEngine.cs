using IntellectBot.Application;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IntellectBot.Configuration;
using IntellectBot.Domain;
using IntellectBot.Llm;
using IntellectBot.Mcp;
using IntellectBot.Infrastructure;

namespace IntellectBot.Agent;

public sealed class AgentEngine(IAgentLlmClient llm, Func<IMcpGateway?> gateway,
    IAgentStateStore state, IReminderPreparation reminders, LlmSettings settings, TimeProvider clock, InteractionState? interactions = null, IReadOnlyList<IToolProposalHandler>? proposalHandlers = null, SafeLog? log = null)
{
    private readonly InteractionState interactionState = interactions ?? new(clock);
    private readonly AgentToolExecutor executor = new(proposalHandlers ?? [new ReminderProposalHandler(reminders, clock)]);
    public void ClearPendingInput(long owner) => interactionState.Clear(owner);

    public async Task<LlmResponse> ReplyAsync(long owner, long updateId, string question,
        UserSettings user, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        AgentStateValidation.ValidateOwner(owner);
        if (updateId < 0)
            throw new ArgumentException("Некорректный update.");
        var requestId = Guid.NewGuid();
        var started = clock.GetUtcNow();
        var audit = new List<ToolAuditEntry>();
        var action = "respond";
        var reason = "Обычный ответ без внешних действий.";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        try
        {
            var choice = interactionState.SelectChoice(owner, question);
            if (choice.Error is not null)
            {
                action = "clarify";
                reason = "Номер варианта не принят или список устарел.";
                return new(choice.Error);
            }
            var zone = await state.GetTimeZoneAsync(owner, token);
            if (ReminderRequestGuard.PastTimeClarification(question, zone, started) is { } past)
            {
                action = "clarify";
                reason = "Явно указанное время сегодня уже прошло в часовом поясе пользователя.";
                return new(past);
            }
            var rules = AgentTimeContext.Rules(started, zone);
            var currentQuestion = choice.Topic is null ? question : $"Покажи данные выбранного варианта: {choice.Topic}";
            var replyKey = choice.SelectionId is { } location ? choice.ToolName + ":" + location.ToString(CultureInfo.InvariantCulture)
                : question.Trim().ToUpperInvariant();
            var pendingReply = interactionState.TakePendingReply(owner, replyKey);
            var initial = ContextBuilder.Build("chat", choice.Topic is null ? history : Array.Empty<ChatMessage>(), currentQuestion, settings.HistoryMaxMessages,
                settings.ContextMaxChars - ContextBuilder.Length(rules));
            var messages = initial.Select(message => JsonDefaults.Element(message)).ToList();
            messages[0] = JsonDefaults.Element(new
            {
                role = "system",
                content = initial[0].Content + "\n" + rules
            });
            var remembered = choice.Topic is null && pendingReply is null
                ? interactionState.ReadAgentContext(owner, question) : Array.Empty<JsonElement>();
            if (remembered.Count > 0)
            {
                var system = messages[0];
                messages = new List<JsonElement> { system };
                messages.AddRange(remembered);
                messages.Add(JsonDefaults.Element(new
                {
                    role = "user",
                    content = currentQuestion
                }));
            }
            var mcp = gateway();
            var tools = mcp?.Catalog.Definitions ?? Array.Empty<McpToolDefinition>();
            if (pendingReply is not null)
            {
                messages = new List<JsonElement> { messages[0] };
                messages.AddRange(pendingReply.Messages);
                audit.AddRange(pendingReply.Tools);
                action = "call_tool";
                reason = "Продолжен ответ по ранее полученным данным MCP.";
            }
            var attempts = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var prepared = new List<PreparedReminder>();
            var clarificationRetried = false;
            var responseRetried = false;
            var publicTexts = new List<string>();
            var requiredEvidence = AgentAnswerPolicy.RequiredEvidence(question) ??
                (remembered.Count > 0 && !AgentAnswerPolicy.IsAcknowledgement(question) ? ToolResultContext.EvidenceKind(remembered) : null);
            if (pendingReply is not null)
                publicTexts.AddRange(ToolResultContext.PublicTexts(messages));
            var receivedReadResult = pendingReply is not null;

            for (var round = 0; round < 4; round++)
            {
                // Цифра — явный выбор пользователя. Используем проверенный вариант без угадывания моделью.
                AgentModelTurn turn;
                try
                {
                    AgentContextBudget.Fit(messages, settings);
                    var modelStarted = Stopwatch.GetTimestamp();
                    turn = round == 0 && choice.Topic is not null && mcp is not null && pendingReply is null
                        ? SelectedToolTurn(choice)
                        : await llm.CompleteAsync(messages, tools, user.Temperature,
                            mcp is not null && attempts < 2, token, user.Model);
                    log?.Write($"Этап агента: request_id={requestId:N} stage=llm duration_ms={Stopwatch.GetElapsedTime(modelStarted).TotalMilliseconds:F0} round={round + 1}");
                }
                catch (DialogueException error) when (prepared.Count > 0)
                {
                    reason = "Подготовка сохранена; остальная часть запроса превышает допустимый контекст.";
                    return PendingResponse(error.Message);
                }
                catch (LlmException error) when (prepared.Count > 0)
                {
                    reason = "Напоминание подготовлено; модель не завершила остальную часть ответа.";
                    return PendingResponse(error.Message);
                }
                catch (LlmException error) when (error.Code == "rate_limit" &&
                    audit.Count > 0 && prepared.Count == 0)
                {
                    interactionState.SavePendingReply(owner, replyKey, messages, audit);
                    throw;
                }
                if (turn.Calls.Count == 0)
                {
                    var needsRepair = turn.Decision == "respond" &&
                        (requiredEvidence is not null && !ToolResultContext.HasEvidence(messages, requiredEvidence) && publicTexts.Count == 0 ||
                         ToolResultContext.IsAmbiguous(messages) ||
                         ReminderRequestGuard.IsRequest(question) && prepared.Count == 0 &&
                            turn.Text is { } answer && AgentAnswerPolicy.AsksConfirmation(answer));
                    if (needsRepair)
                    {
                        if (!responseRetried && tools.Count > 0 && attempts < 2)
                        {
                            responseRetried = true;
                            messages.Add(JsonDefaults.Element(new
                            {
                                role = "system",
                                content = ToolResultContext.IsAmbiguous(messages)
                                ? "Получен неоднозначный результат источника. Выбери реальные ID из candidates и вызови функцию уточнения. Не повторяй поиск, не придумывай варианты."
                                : requiredEvidence is not null
                                    ? "Нужны проверенные данные источника. Вызови соответствующий объявленный инструмент; без его результата не придумывай сведения."
                                    : "Не запрашивай подтверждение обычным текстом. Подготовь действие через объявленный инструмент; кнопки покажет приложение."
                            }));
                            continue;
                        }
                        return new(requiredEvidence is not null ? "Не удалось проверить сведения по источнику. Уточните полное имя или тему; неподтверждённые факты не приводятся."
                            : "Не удалось подготовить напоминание. Укажите действие и точное время; создание подтверждается только кнопкой.");
                    }
                    if (turn.Decision == "clarify")
                    {
                        action = "clarify";
                        reason = "Модель запросила недостающие параметры без выполнения инструмента.";
                    }
                    var text = turn.Text ?? throw new LlmException("empty_response");
                    if (turn.Choices.Count > 0)
                    {
                        var offered = interactionState.OfferModelChoices(
                            owner, turn.Choices, turn.Text);

                        if (offered is null)
                        {
                            // Один раз возвращаем ошибку проверки модели для исправления списка.
                            if (!clarificationRetried && round < 3 &&
                                turn.Message.TryGetProperty("tool_calls", out var rejectedCalls) &&
                                rejectedCalls.ValueKind == JsonValueKind.Array &&
                                rejectedCalls.GetArrayLength() == 1)
                            {
                                clarificationRetried = true;
                                messages.Add(turn.Message);

                                messages.Add(JsonDefaults.Element(new
                                {
                                    role = "tool",
                                    tool_call_id = rejectedCalls[0].GetProperty("id").GetString(),
                                    content = JsonDefaults.Text(new
                                    {
                                        success = false,
                                        error = "invalid_clarification",
                                        message =
                                            "Список вариантов отклонён. Исправь уточнение по уже " +
                                            "полученному candidates. Каждый id должен точно " +
                                            "совпадать с id кандидата, а не его номером в списке. " +
                                            "Подписи должны различаться и не содержать координат. " +
                                            "Не вызывай погодный инструмент повторно " +
                                            "для исправления списка."
                                    })
                                }));

                                continue;
                            }

                            action = "clarify";
                            reason = "Модель не сформировала корректный список вариантов.";

                            return new(
                                "Не удалось сформировать корректный список городов. " +
                                "Уточните название, страну и регион.");
                        }

                        text = offered;
                        action = "clarify";
                        reason = "Модель выбрала различимые варианты из результата MCP для уточнения.";
                    }
                    if (turn.Decision == "respond" && publicTexts.Count > 0)
                    {
                        var reference = EvidencePresentation.Render(messages, text);
                        var original = reference is not null ? EvidencePresentation.Source(messages).GetProperty("public_text").GetString() : null;
                        text = string.Join("\n\n", publicTexts.Distinct(StringComparer.Ordinal)
                            .Select(value => value == original ? reference! : value));
                    }
                    if (attempts > 0 || choice.Topic is not null || turn.Choices.Count > 0)
                        interactionState.RememberAgentContext(owner, messages.Append(JsonDefaults.Element(new
                        {
                            role = "assistant",
                            content = text
                        })));
                    else if (turn.Decision == "respond")
                        interactionState.Clear(owner);
                    return new(text)
                    {
                        PendingActions = prepared.AsReadOnly(),
                        IncludeTextWithPendingActions = prepared.Count > 0
                    };
                }
                if (turn.Calls.Count > 8)
                    throw new LlmException("invalid_response");
                messages.Add(turn.Message);
                foreach (var call in turn.Calls)
                {
                    if (!seen.Add(call.Id))
                        throw new LlmException("invalid_response");
                    if (attempts >= 2)
                    {
                        reason = "Достигнут лимит двух действий; дополнительные вызовы отклонены.";
                        AddResult(call, new
                        {
                            success = false,
                            error = "limit_exceeded",
                            instruction = "Заверши обычным текстом."
                        });
                        continue;
                    }
                    attempts++;
                    if (!(round == 0 && choice.Topic is not null) &&
                        !interactionState.AcceptsSelection(owner, call.Name, JsonDefaults.ParseObject(call.Arguments)))
                    {
                        AddResult(call, new
                        {
                            success = false,
                            error = "unknown_selection",
                            message = "ID не получен от источника. Используй точный selection_arguments из показанного candidates либо повтори поиск по имени без ID."
                        });
                        continue;
                    }
                    if (call.Name == "add_reminder" &&
                        ReminderRequestGuard.ArgumentError(
                            question,
                            JsonDefaults.ParseObject(call.Arguments),
                            zone,
                            started) is { } mismatch)
                    {
                        action = "clarify";
                        reason = "Аргументы напоминания расходятся с исходным запросом.";

                        audit.Add(new(
                            call.Name,
                            "Некорректные аргументы скрыты.",
                            "rejected",
                            "not_called"));

                        AddResult(call, new
                        {
                            success = false,
                            error = "invalid_arguments",
                            message = mismatch
                        });

                        continue;
                    }
                    var toolStarted = Stopwatch.GetTimestamp();
                    var execution = await executor.ExecuteAsync(call, tools, mcp, owner, updateId, attempts - 1, zone, token);
                    log?.Write($"Этап агента: request_id={requestId:N} stage=mcp tool={call.Name} duration_ms={Stopwatch.GetElapsedTime(toolStarted).TotalMilliseconds:F0} status={execution.Audit.Execution}");
                    audit.Add(execution.Audit);
                    action = execution.Action;
                    reason = execution.Reason;
                    if (execution.UserError is { } errorText)
                    {
                        AddResult(call, new
                        {
                            success = false,
                            error = "validation_failed",
                            message = errorText
                        });
                        if (prepared.Count > 0)
                            return PendingResponse(errorText);
                        if (receivedReadResult)
                            continue;
                        return new(errorText);
                    }
                    if (execution.Pending is { } pending)
                        prepared.Add(pending);
                    else
                        receivedReadResult = true;
                    if (execution.SourceChoices is { } source)
                        interactionState.RememberToolSource(owner, call.Name, JsonDefaults.ParseObject(call.Arguments), source);
                    if (execution.PublicText is { } displayText)
                        publicTexts.Add(displayText);
                    // Любой результат MCP возвращается в модель.
                    AddResult(call, execution.ModelResult);
                }
                if (prepared.Count > 0 && !receivedReadResult)
                    return PendingResponse();
            }
            if (prepared.Count > 0)
                return PendingResponse("Остальная часть запроса не завершена: достигнут лимит действий.");
            reason = "Модель продолжила вызывать инструменты после достижения лимита.";
            return new("Достигнут лимит действий для одного сообщения. Уточните запрос или отправьте следующий вопрос.");

            LlmResponse PendingResponse(string? extra = null)
            {
                var previews = prepared.Select(item => ReminderConfirmationService.ReminderDetails(item));
                return new(extra ?? ("Подготовлено напоминание:\n\n" + string.Join("\n\n", previews) +
                    "\n\nНапоминание ещё не создано: требуется отдельное подтверждение."))
                {
                    PendingActions = prepared.AsReadOnly(),
                    IncludeTextWithPendingActions = extra is not null
                };
            }

            void AddResult(AgentToolCall call, object result) => messages.Add(JsonDefaults.Element(new
            {
                role = "tool",
                tool_call_id = call.Id,
                content = JsonDefaults.Text(result)
            }));
        }
        catch (LlmException error)
        {
            reason = error.Code == "rate_limit"
                ? "API ограничил запрос; полученные данные MCP сохранены для короткого повторного запроса."
                : "Модель не завершила ответ; действие без подтверждения не выполнялось.";
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            reason = "Превышено время обработки запроса.";
            throw new LlmException("timeout");
        }
        catch
        {
            reason = "Обработка не завершена; внутренние данные скрыты.";
            throw;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                using var saveTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                saveTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await state.SaveAuditAsync(owner, new(requestId, started, action, audit.ToArray(), reason), saveTimeout.Token);
            }
        }
    }

    private static AgentModelTurn SelectedToolTurn(ToolChoiceResult choice)
    {
        var callId = "selection_" + Guid.NewGuid().ToString("N");
        var arguments = choice.Arguments is { } ready ? JsonDefaults.Text(ready) : choice.SelectionId is { } id
            ? JsonDefaults.Text(new
            {
                city = choice.Topic,
                locationId = id.ToString(CultureInfo.InvariantCulture)
            })
            : JsonDefaults.Text(new
            {
                city = choice.Topic
            });
        var call = new AgentToolCall(callId, choice.ToolName, arguments);
        return new(AgentToolProtocol.CallMessage([call]), null, [call])
        {
            Decision = "call_tool"
        };
    }
}
