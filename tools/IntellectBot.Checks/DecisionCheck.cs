using System.Text.Json;
using IntellectBot.Agent;
using IntellectBot.Configuration;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;
using IntellectBot.Llm;
using IntellectBot.Mcp;

namespace IntellectBot.Checks;

public static class DecisionCheck
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var settings = AppSettings.Load();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await using var mcp = await BotMcpClient.ConnectAsync(settings.McpServerPath, null,
                TimeProvider.System, settings.ScheduleFile, settings.ScheduleTimeZone, timeout.Token);
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            var llm = new AgentLlmClient(http, settings.Llm, new SafeLog(settings));
            var messages = new[]
            {
                JsonSerializer.SerializeToElement(new { role = "system", content =
                    $"Ты AI-ассистент. Часовой пояс пользователя Europe/Moscow. Сейчас {DateTimeOffset.UtcNow:O}. " +
                    "Запрос на создание напоминания с известными текстом, временем и поясом требует add_reminder. " +
                    "Не спрашивай подтверждение обычным текстом: его формирует приложение после подготовки." }),
                JsonSerializer.SerializeToElement(new { role = "user", content =
                    "Создай напоминание Проверить код на 2030-01-15 в 18:00 по моему часовому поясу." })
            };
            var result = await llm.CompleteAsync(messages, mcp.Catalog.Definitions, 0.3, true, timeout.Token);
            var reminderSelected = result.Calls.Count == 1 &&
                result.Calls[0].Name == "add_reminder";

            Console.WriteLine(
                $"LLM: decision={result.Decision}; calls={result.Calls.Count}; add_reminder={reminderSelected}.");

            if (!reminderSelected)
                throw new InvalidOperationException();
            var definition = mcp.Catalog.Definitions.Single(tool => tool.Name == "add_reminder");
            using var argumentDocument = JsonDocument.Parse(result.Calls[0].Arguments);
            var fields = argumentDocument.RootElement.EnumerateObject().Select(property =>
            {
                var name = property.Name is "text" or "remind_at" or "city" or "date"
                    ? property.Name : "<extra>";
                var length = property.Value.ValueKind == JsonValueKind.String
                    ? ContextBuilder.Length(property.Value.GetString()!) : 0;
                return $"{name}:{property.Value.ValueKind}(length={length})";
            });
            Console.WriteLine("LLM: argument_fields=" + string.Join(", ", fields));
            var args = AgentInput.Validate(definition, result.Calls[0].Arguments);
            var draft = ReminderValidation.Parse(args.GetProperty("text").GetString(), args.GetProperty("remind_at").GetString(), "Europe/Moscow");
            var textMatches = draft.Text == "Проверить код";
            var timeMatches = draft.RemindAt ==
                new DateTimeOffset(2030, 1, 15, 18, 0, 0, TimeSpan.FromHours(3));

            Console.WriteLine(
                $"LLM: text_matches={textMatches}; time_matches={timeMatches}.");

            if (!textMatches || !timeMatches)
                throw new InvalidOperationException();
            Console.WriteLine("LLM: структурированное add_reminder и точные параметры — OK. Запись не выполнялась.");
            return 0;
        }
        catch (Exception error)
        {
            var code = error is ReminderException reminderError
                ? reminderError.Code : error.GetType().Name;

            Console.Error.WriteLine(
                $"Проверка решения LLM не пройдена: {code}. Секреты и ответ API скрыты.");
            return 1;
        }
    }
}
