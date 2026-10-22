using IntellectBot.Agent;
using IntellectBot.Application;
using IntellectBot.Configuration;
using IntellectBot.Data;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;
using IntellectBot.Llm;
using IntellectBot.Mcp;
using IntellectBot.Telegram;
using Npgsql;
namespace IntellectBot.Hosting;

// Единая точка сборки приложения: репозитории → сценарии → обработчик Telegram.
public static class BotComposition
{
    public static UpdateHandler CreateHandler(AppSettings settings, NpgsqlDataSource source, ITelegramApi api,
        HttpClient llmHttp, SafeLog log, Func<IConfirmedReminderGateway?> gateway)
    {
        var clock = TimeProvider.System;
        var state = new AgentStateStore(source);
        var interactions = new InteractionState(clock);
        var reminders = new ReminderStore(source, clock);
        var agent = new AgentEngine(new AgentLlmClient(llmHttp, settings.Llm, log), gateway, state, reminders, settings.Llm, clock, interactions, log: log);
        var dialogue = new DialogueService(new DialogueStore(source), new LlmClient(llmHttp, settings.Llm, log), settings.Llm, agent, state, clock);
        var ui = new AgentUiService(state, reminders, gateway, clock);
        var schedule = new ScheduleEditor(new PersonalScheduleStore(source), state, clock);
        return new UpdateHandler(api, dialogue, log, ui, new WorkspaceController(schedule), interactions);
    }
}
