using System.Collections.Concurrent;
using IntellectBot.Domain;
namespace IntellectBot.Application;

public sealed record ScheduleDraft(Guid Id, DateTimeOffset ExpiresAt, IReadOnlyList<ScheduleEntry> Entries, DateOnly? DeleteDate, bool Append = false);
public sealed record ScheduleInputResult(string? Prompt, ScheduleDraft? Draft);

// Редактор расписания: временный ввод, просмотр и подтверждение. SQL выполняет репозиторий.
public sealed class ScheduleEditor(IPersonalScheduleStore store, IAgentStateStore state, TimeProvider clock)
{
    private readonly ConcurrentDictionary<long, (DateTimeOffset Expires, bool Delete)> inputs = new();
    private readonly ConcurrentDictionary<long, ScheduleDraft> drafts = new();
    private readonly ConcurrentDictionary<long, List<string>> guided = new();
    public void BeginGuided(long owner)
    {
        Begin(owner, false);
        guided[owner] = [];
    }
    public ScheduleInputResult Accept(long owner, string text)
    {
        if (!guided.TryGetValue(owner, out var fields))
            return new(null, Preview(owner, text));
        if (!AwaitingInput(owner))
        {
            Cancel(owner);
            throw new ArgumentException("Время ввода истекло. Откройте расписание заново.");
        }
        text = text.Trim();
        if (text.Contains('|') || text.Contains('\n') || text.Contains('\0') || text.Length > 200)
            throw new ArgumentException("Пришлите одно значение до 200 символов без вертикальной черты.");
        if (fields.Count == 0)
            _ = ScheduleInput.Date(text);
        if (fields.Count is 1 or 2)
        {
            if (!TimeOnly.TryParseExact(text, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var time))
                throw new ArgumentException("Укажите время в формате ЧЧ:ММ, например 09:00.");
            if (fields.Count == 2 && time <= TimeOnly.ParseExact(fields[1], "HH:mm", System.Globalization.CultureInfo.InvariantCulture))
                throw new ArgumentException("Конец занятия должен быть позже начала.");
        }
        if (fields.Count == 3 && text.Length == 0)
            throw new ArgumentException("Введите название занятия.");
        fields.Add(fields.Count == 4 && text == "-" ? "" : text);
        inputs[owner] = (clock.GetUtcNow().AddMinutes(5), false);
        if (fields.Count < 5)
            return new(fields.Count switch
            {
                1 => "Во сколько начинается? Например: 09:00",
                2 => "Во сколько заканчивается? Например: 10:30",
                3 => "Как называется занятие?",
                _ => "Где проходит занятие? Если место не нужно, отправьте минус: -"
            }, null);
        // Дата + начало + конец + название + место образуют одну проверенную запись.
        var draft = Preview(owner, string.Join(" | ", fields)) with
        {
            Append = true
        };
        drafts[owner] = draft;
        guided.TryRemove(owner, out _);
        return new(null, draft);
    }
    public async Task<string?> GetTimeZoneAsync(long owner, CancellationToken ct) => await state.GetTimeZoneAsync(owner, ct);
    public void Begin(long owner, bool delete)
    {
        RemoveExpired();
        drafts.TryRemove(owner, out _);
        guided.TryRemove(owner, out _);
        inputs[owner] = (clock.GetUtcNow().AddMinutes(5), delete);
    }
    public void Cancel(long owner)
    {
        inputs.TryRemove(owner, out _);
        drafts.TryRemove(owner, out _);
        guided.TryRemove(owner, out _);
    }
    public bool AwaitingInput(long owner) => inputs.TryGetValue(owner, out var pending) && pending.Expires > clock.GetUtcNow();
    public ScheduleDraft Preview(long owner, string text)
    {
        if (!inputs.TryGetValue(owner, out var pending) || pending.Expires <= clock.GetUtcNow())
            throw new ArgumentException("Время ввода истекло. Откройте расписание заново.");
        var draft = pending.Delete ? new ScheduleDraft(Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5), [], ScheduleInput.Date(text.Trim())) :
            new ScheduleDraft(Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5), ScheduleInput.Parse(text), null);
        drafts[owner] = draft;
        inputs.TryRemove(owner, out _);
        return draft;
    }
    public async Task<bool> ConfirmAsync(long owner, Guid id, CancellationToken ct)
    {
        // Чаты последовательно обрабатываются UpdateHandler; ID привязывает кнопку к конкретному просмотру.
        if (!drafts.TryGetValue(owner, out var draft) || draft.Id != id || draft.ExpiresAt <= clock.GetUtcNow())
            return false;
        // Запрещаем сохранение прошедших дат по часовому поясу пользователя.
        // Удаление старых занятий остаётся доступным.
        if (draft.DeleteDate is null)
        {
            var zone = await state.GetTimeZoneAsync(owner, ct)
                ?? throw new ArgumentException(
                    "Сначала выберите часовой пояс в настройках.");

            var localNow = TimeZoneInfo.ConvertTime(
                clock.GetUtcNow(),
                TimeZoneInfo.FindSystemTimeZoneById(zone));

            var today = DateOnly.FromDateTime(localNow.DateTime);

            if (draft.Entries.Any(entry => entry.Date < today))
                throw new ArgumentException(
                    "Нельзя сохранить расписание на прошедшую дату. Укажите сегодняшнюю или будущую дату.");
        }
        if (draft.DeleteDate is { } date)
            await store.DeleteDayAsync(owner, date, ct);
        else if (draft.Append)
            await store.AddAsync(owner, draft.Entries, ct);
        else
            await store.ReplaceDaysAsync(owner, draft.Entries, ct);
        drafts.TryRemove(owner, out _);
        await RecordAsync(owner, draft.DeleteDate is null ? "Пользователь подтвердил обновление своего расписания. Остальные даты сохранены." :
            "Пользователь подтвердил удаление занятий на выбранную дату.", ct);
        return true;
    }
    public async Task<IReadOnlyList<ScheduleEntry>> WeekAsync(long owner, CancellationToken ct)
    {
        var zone = await state.GetTimeZoneAsync(owner, ct) ?? throw new ArgumentException("Сначала выберите часовой пояс в настройках.");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var result = await store.ReadAsync(owner, monday, monday.AddDays(6), ct);
        await RecordAsync(owner, "Показано личное расписание текущей недели из PostgreSQL.", ct, "schedule_read");
        return result;
    }
    private Task RecordAsync(long owner, string reason, CancellationToken ct, string action = "schedule_write") => state.SaveAuditAsync(owner,
        new(Guid.NewGuid(), clock.GetUtcNow(), action, [], reason), ct);
    private void RemoveExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var item in inputs.Where(x => x.Value.Expires <= now))
        {
            inputs.TryRemove(item.Key, out _);
            guided.TryRemove(item.Key, out _);
        }
        foreach (var item in drafts.Where(x => x.Value.ExpiresAt <= now))
            drafts.TryRemove(item.Key, out _);
    }
}
