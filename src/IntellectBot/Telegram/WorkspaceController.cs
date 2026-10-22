using System.Collections.Concurrent;
using IntellectBot.Application;
using IntellectBot.Domain;
namespace IntellectBot.Telegram;

public sealed record WorkspaceScreen(string Text, InlineKeyboard Keyboard);

// Навигация личного расписания. Владелец приходит только из проверенного Telegram-чата.
public sealed class WorkspaceController(ScheduleEditor editor)
{
    public bool AwaitingInput(long owner) => editor.AwaitingInput(owner);
    private readonly ConcurrentDictionary<long, (string Prefix, DateTimeOffset Expires)> origins = new();
    public void Cancel(long owner)
    {
        editor.Cancel(owner);
        origins.TryRemove(owner, out _);
    }
    private static InlineKeyboard Menu(string prefix) => new([
        new InlineButton[]{new("На этой неделе",$"workspace:{prefix}:week")},
        new InlineButton[]{new("Добавить занятие",$"workspace:{prefix}:add"),new("Вставить список",$"workspace:{prefix}:import")},
        new InlineButton[]{new("Удалить день",$"workspace:{prefix}:delete")},
        new InlineButton[]{new("Назад",$"{prefix}:settings")}
    ]);
    private static InlineKeyboard CancelButton(string prefix) => new([new InlineButton[] { new("Отмена", $"workspace:{prefix}:home") }]);
    public async Task<WorkspaceScreen> OpenAsync(long owner, string action, CancellationToken ct)
    {
        var prefix = "menu";
        if (action.StartsWith("menu:", StringComparison.Ordinal) || action.StartsWith("settings:", StringComparison.Ordinal))
        {
            var parts = action.Split(':', 2);
            prefix = parts[0];
            action = parts[1];
        }
        foreach (var entry in origins.Where(entry => entry.Value.Expires <= DateTimeOffset.UtcNow))
            origins.TryRemove(entry.Key, out _);
        origins[owner] = (prefix, DateTimeOffset.UtcNow.AddMinutes(5));
        try
        {
            if (action == "home")
            {
                editor.Cancel(owner);
                return new("Моё расписание\n\nЗдесь хранятся только ваши занятия. Добавьте их, а затем спрашивайте о расписании обычным сообщением.\n\nЗанятия задаются на конкретные даты и сохраняются после перезапуска.", Menu(prefix));
            }
            if (action is "add" or "import" or "delete")
            {
                if (await editor.GetTimeZoneAsync(owner, ct) is null)
                    return new("Сначала укажите часовой пояс: Настройки → Часовой пояс.", Menu(prefix));
                if (action == "add")
                {
                    editor.BeginGuided(owner);
                    return new("На какую дату добавить занятие?\nНапример: 2030-01-15", CancelButton(prefix));
                }
                editor.Begin(owner, action == "delete");
                return new(action == "delete" ? "Пришлите дату, занятия на которую нужно удалить.\nНапример: 2030-01-15\n\nУдаление потребует подтверждения." :
                    "Пришлите занятия: одна строка — одно занятие.\n\nДата | начало | конец | предмет | место\n2030-01-15 | 09:00 | 10:30 | Программирование | Аудитория 101\n\nДо 20 строк. Место можно оставить пустым после последней черты.\nДля указанных дат список будет заменён после подтверждения. Остальные даты сохранятся.", CancelButton(prefix));
            }
            if (action == "week")
            {
                editor.Cancel(owner);
                var entries = await editor.WeekAsync(owner, ct);
                var lines = entries.Select(Describe).ToArray();
                var text = "На этой неделе\n\n" + (lines.Length == 0 ? "Занятий пока нет. Добавьте своё расписание." : string.Join("\n", lines));
                if (text.Length > 3500)
                    text = MessageFormatter.Split(text, 3400).First() + "\n\nМного занятий. Спросите ассистента о конкретной дате.";
                return new(text, Menu(prefix));
            }
            if (action.StartsWith("save:", StringComparison.Ordinal) && Guid.TryParseExact(action[5..], "N", out var id))
                return new(await editor.ConfirmAsync(owner, id, ct) ? "Расписание обновлено." :
                    "Это подтверждение уже использовано или истекло. Откройте расписание заново.", Menu(prefix));
            return new("Эта кнопка устарела. Откройте расписание заново.", Menu(prefix));
        }
        catch (ArgumentException error) { return new(error.Message, Menu(prefix)); }
    }
    public WorkspaceScreen Input(long owner, string text)
    {
        var prefix = origins.TryGetValue(owner, out var origin) ? origin.Prefix : "menu";
        origins[owner] = (prefix, DateTimeOffset.UtcNow.AddMinutes(5));
        try
        {
            var result = editor.Accept(owner, text);
            if (result.Prompt is not null)
                return new(result.Prompt, CancelButton(prefix));
            var draft = result.Draft!;
            var description = draft.DeleteDate is { } date ? $"Удалить все занятия на {date:yyyy-MM-dd}?" :
                "Сохранить расписание?\n\n" + string.Join("\n", draft.Entries.Select(Describe)) + (draft.Append ? "\n\nНовое занятие будет добавлено. Остальные сохранятся." : "\n\nЗанятия на перечисленные даты будут заменены.");
            // Полный предварительный просмотр должен помещаться в сообщение Telegram.
            if (description.Length > 3800)
            {
                editor.Begin(owner, false);
                return new("Предпросмотр слишком длинный. Пришлите меньше занятий.", CancelButton(prefix));
            }
            return new(description, new([new InlineButton[] { new("Подтвердить", $"workspace:{prefix}:save:{draft.Id:N}"), new("Отмена", $"workspace:{prefix}:home") }]));
        }
        catch (ArgumentException error) { return new(error.Message, CancelButton(prefix)); }
    }
    private static string Describe(ScheduleEntry entry) =>
        $"{entry.Date:yyyy-MM-dd} · {entry.Start:HH:mm}–{entry.End:HH:mm} · {entry.Title}" +
        (entry.Location.Length == 0 ? "" : " · " + entry.Location);
}
