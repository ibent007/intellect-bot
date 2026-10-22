using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntellectBot.Agent;
using IntellectBot.Domain;
using IntellectBot.Infrastructure;

namespace IntellectBot.Application;

public sealed record TimeZoneInput(Guid Id, long MessageId, bool ShowMenuBack, DateTimeOffset ExpiresAt);
public sealed record ToolChoice(string Topic, int? SelectionId = null, string? DisplayName = null,
    string ToolName = "get_weather", JsonElement? Arguments = null);
public sealed record ToolChoiceResult(string? Topic = null, string? Error = null, int? SelectionId = null,
    string ToolName = "get_weather", JsonElement? Arguments = null);

// Короткие сценарии ввода одного пользователя. Данные живут пять минут; записи БД сюда не попадают
public sealed class InteractionState(TimeProvider clock)
{
    private sealed record ChoiceList(ToolChoice[] Items, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<long, TimeZoneInput> zones = new();
    private readonly ConcurrentDictionary<long, PreparedReminder[]> confirmations = new();
    public void RememberConfirmations(long owner, IReadOnlyList<PreparedReminder> actions) => confirmations[owner] = actions.ToArray();
    public IReadOnlyList<PreparedReminder> PendingConfirmations(long owner) => confirmations.GetValueOrDefault(owner, [])
        .Where(action => action.ExpiresAt > clock.GetUtcNow()).ToArray();
    public void ForgetConfirmation(long owner, Guid actionId)
    {
        if (confirmations.TryGetValue(owner, out var actions))
            confirmations[owner] = actions.Where(action => action.Id != actionId).ToArray();
    }
    private readonly ConcurrentDictionary<long, ChoiceList> choices = new();
    private sealed record AgentContext(JsonElement[] Messages, DateTimeOffset ExpiresAt);
    private sealed record ToolSource(string Topic, JsonElement Data, DateTimeOffset ExpiresAt, string ToolName);
    private readonly ConcurrentDictionary<long, AgentContext> contexts = new();
    private readonly ConcurrentDictionary<long, ToolSource> sources = new();
    public sealed record PendingReply(string Key, JsonElement[] Messages, ToolAuditEntry[] Tools, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<long, PendingReply> pendingReplies = new();

    public void SavePendingReply(long owner, string key, IEnumerable<JsonElement> messages, IEnumerable<ToolAuditEntry> tools)
    {
        RemoveExpired();
        var copy = messages.Where(message => message.GetProperty("role").GetString() != "system")
            .Select(message => message.Clone()).ToArray();
        if (copy.Sum(message => JsonDefaults.Text(message).Length) <= 16000)
            pendingReplies[owner] = new(key, copy, tools.ToArray(), clock.GetUtcNow().AddMinutes(2));
    }

    public PendingReply? TakePendingReply(long owner, string key)
    {
        RemoveExpired();
        return pendingReplies.TryRemove(owner, out var value) && value.Key == key ? value : null;
    }

    public IReadOnlyList<JsonElement> ReadAgentContext(long owner, string? question = null)
    {
        RemoveExpired();
        if (question is not null && (question.Contains('?') || Regex.IsMatch(question.Trim(),
                @"^(?:расскажи|объясни|напиши|переведи|напомни|создай|добавь|кто|что|как|почему|сколько)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
        {
            contexts.TryRemove(owner, out _);
            sources.TryRemove(owner, out _);
            return [];
        }
        return contexts.TryGetValue(owner, out var context) ? context.Messages : [];
    }

    public void RememberAgentContext(long owner, IEnumerable<JsonElement> messages)
    {
        RemoveExpired();
        var copy = messages.Where(message => message.GetProperty("role").GetString() != "system")
            .Select(message => message.Clone()).ToList();
        // Для продолжения достаточно последнего хода. Нумерация городов хранится отдельно.
        var lastUser = copy.FindLastIndex(message => message.GetProperty("role").GetString() == "user");
        if (lastUser > 0)
            copy.RemoveRange(0, lastUser);
        while (copy.Sum(message => JsonDefaults.Text(message).Length) > 16000 || copy.Count > 12)
        {
            var nextTurn = copy.FindIndex(1, message => message.GetProperty("role").GetString() == "user");
            if (nextTurn < 0)
            {
                contexts.TryRemove(owner, out _);
                return;
            }
            copy.RemoveRange(0, nextTurn);
        }
        contexts[owner] = new(copy.ToArray(), clock.GetUtcNow().AddMinutes(5));
    }

    public void RememberToolSource(long owner, string toolName, JsonElement arguments, JsonElement data)
    {
        RemoveExpired();
        choices.TryRemove(owner, out _);
        var topic = arguments.TryGetProperty("city", out var city) ? city.GetString() :
            arguments.TryGetProperty("topic", out var title) ? title.GetString() : "Выбранный вариант";
        sources[owner] = new(topic ?? "Выбранный вариант", data.Clone(), clock.GetUtcNow().AddMinutes(5), toolName);
    }

    public bool AcceptsSelection(long owner, string toolName, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return true;
        var hasSelection = arguments.TryGetProperty("locationId", out var location) && location.ValueKind == JsonValueKind.String ||
            arguments.TryGetProperty("pageId", out var page) && page.ValueKind == JsonValueKind.String;
        if (!hasSelection)
            return true;
        if (!sources.TryGetValue(owner, out var source) || source.ToolName != toolName || source.ExpiresAt <= clock.GetUtcNow())
            return false;
        if (!source.Data.TryGetProperty("candidates", out var candidates))
            return false;
        return candidates.EnumerateArray().Any(candidate =>
        {
            if (candidate.TryGetProperty("selection_arguments", out var expected) && expected.ValueKind == JsonValueKind.Object)
                return JsonElement.DeepEquals(expected, arguments);
            // Совместимость с каталогами, где старый контракт выбора содержит только ID.
            return arguments.TryGetProperty("locationId", out var legacy) && legacy.ValueKind == JsonValueKind.String &&
                int.TryParse(legacy.GetString(), out var id) && candidate.GetProperty("id").GetInt32() == id;
        });
    }

    public string? OfferModelChoices(long owner, IReadOnlyList<AgentChoice> choices, string? question = null)
    {
        if (!sources.TryGetValue(owner, out var source) || source.ExpiresAt <= clock.GetUtcNow() ||
            !source.Data.TryGetProperty("candidates", out var raw) || choices.Count == 0)
            return null;
        var candidates = raw.EnumerateArray().ToDictionary(item => item.GetProperty("id").GetInt32());
        var offered = new List<ToolChoice>();
        foreach (var choice in choices)
        {
            if (!candidates.TryGetValue(choice.Id, out var candidate))
                return null;
            // Подпись берём из MCP, а не из текста модели: страна и название не могут подмениться.
            var label = candidate.TryGetProperty("display_name", out var display) && display.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(display.GetString()) ? display.GetString()! :
                string.Join(", ", new[] { "name", "country", "admin1", "admin2", "admin3", "admin4" }
                    .Select(name => candidate.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(label) || label.Length > 250 || label.Any(char.IsControl) ||
                Regex.IsMatch(label, @"\d{1,3}[.,]\d{3,}") || label.Contains("координат", StringComparison.OrdinalIgnoreCase))
                return null;
            JsonElement? args = candidate.TryGetProperty("selection_arguments", out var selectedArgs) && selectedArgs.ValueKind == JsonValueKind.Object
                ? selectedArgs.Clone() : null;
            offered.Add(new(source.Topic, choice.Id, label, source.ToolName, args));
        }
        if (offered.Select(item => item.DisplayName!.ToUpperInvariant()).Distinct().Count() != offered.Count)
            return null;
        return OfferChoices(owner, offered, "Какой вариант вы имеете в виду?");
    }

    public TimeZoneInput BeginTimeZone(long owner, long messageId, bool showMenuBack)
    {
        RemoveExpired();
        var input = new TimeZoneInput(Guid.NewGuid(), messageId, showMenuBack, clock.GetUtcNow().AddMinutes(5));
        zones[owner] = input;
        choices.TryRemove(owner, out _);
        return input;
    }

    public TimeZoneInput? GetTimeZone(long owner) => zones.GetValueOrDefault(owner);
    public bool IsExpired(TimeZoneInput input) => input.ExpiresAt <= clock.GetUtcNow();
    public void ClearTimeZone(long owner) => zones.TryRemove(owner, out _);

    public string OfferChoices(long owner, IEnumerable<string> cities) =>
        OfferChoices(owner, cities.Select(city => new ToolChoice(city)));

    public string OfferChoices(long owner, IEnumerable<ToolChoice> cities, string? question = null)
    {
        RemoveExpired();

        var options = cities
            .DistinctBy(city => (city.Topic, city.SelectionId))
            .Take(10)
            .ToArray();

        choices[owner] = new(options, clock.GetUtcNow().AddMinutes(5));

        return (string.IsNullOrWhiteSpace(question) ? "Какой город вы имеете в виду?" : question.Trim()) + "\n\n" +
            string.Join("\n", options.Select(
                (city, index) => $"{index + 1}. {city.DisplayName ?? city.Topic}")) +
            "\n\nОтправьте номер варианта. Чтобы сменить тему, задайте новый вопрос.";
    }

    public ToolChoiceResult SelectChoice(long owner, string text)
    {
        text = text.Trim();
        if (!choices.TryGetValue(owner, out var options))
            return new();
        var numeric = text.Length > 0 && text.All(char.IsAsciiDigit);
        var ordinal = Regex.Match(text.ToLowerInvariant(),
            @"^(?:а\s+)?(?:во?\s+)?(один|перв(?:ый|ом)|два|втор(?:ой|ом)|три|трет(?:ий|ьем)|четыре|четв[её]рт(?:ый|ом)|пять|пят(?:ый|ом)|шесть|шест(?:ой|ом)|семь|седьм(?:ой|ом)|восемь|восьм(?:ой|ом)|девять|девят(?:ый|ом)|десять|десят(?:ый|ом)|\d+)(?:\s+вариант(?:е)?)?\??$");
        if (ordinal.Success)
        {
            var word = ordinal.Groups[1].Value;
            var numberWord = word.StartsWith("перв") || word == "один" ? 1 :
                word.StartsWith("втор") || word == "два" ? 2 :
                word.StartsWith("трет") || word == "три" ? 3 :
                word.StartsWith("чет") ? 4 : word.StartsWith("пят") ? 5 :
                word.StartsWith("шест") ? 6 : word.StartsWith("се") ? 7 :
                word.StartsWith("вос") ? 8 : word.StartsWith("дев") ? 9 : word.StartsWith("дес") ? 10 : 0;
            text = numberWord > 0 ? numberWord.ToString(CultureInfo.InvariantCulture) : word;
            numeric = true;
        }
        if (options.ExpiresAt <= clock.GetUtcNow())
        {
            choices.TryRemove(owner, out _);
            return numeric ? new(Error: "Список городов устарел. Спросите погоду ещё раз.") : new();
        }
        if (numeric)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                number < 1 || number > options.Items.Length)
                return new(Error: $"Выберите номер от 1 до {options.Items.Length} или задайте новый вопрос.");
            var selected = options.Items[number - 1];
            return new(selected.Topic, SelectionId: selected.SelectionId, ToolName: selected.ToolName, Arguments: selected.Arguments);
        }
        choices.TryRemove(owner, out _);
        var matches = options.Items
            .Where(city => string.Equals(
                city.Topic, text, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length == 1
            ? new(matches[0].Topic, SelectionId: matches[0].SelectionId, ToolName: matches[0].ToolName, Arguments: matches[0].Arguments)
            : new();
    }

    public void Clear(long owner)
    {
        zones.TryRemove(owner, out _);
        confirmations.TryRemove(owner, out _);
        choices.TryRemove(owner, out _);
        contexts.TryRemove(owner, out _);
        sources.TryRemove(owner, out _);
        pendingReplies.TryRemove(owner, out _);
    }

    private void RemoveExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var entry in confirmations.Where(entry => entry.Value.All(action => action.ExpiresAt <= now)))
            confirmations.TryRemove(entry.Key, out _);
        foreach (var entry in pendingReplies.Where(entry => entry.Value.ExpiresAt <= now))
            pendingReplies.TryRemove(entry.Key, out _);
        foreach (var entry in zones.Where(entry => entry.Value.ExpiresAt <= now))
            zones.TryRemove(entry.Key, out _);
        foreach (var entry in choices.Where(entry => entry.Value.ExpiresAt <= now))
            choices.TryRemove(entry.Key, out _);
        foreach (var entry in contexts.Where(entry => entry.Value.ExpiresAt <= now))
            contexts.TryRemove(entry.Key, out _);
        foreach (var entry in sources.Where(entry => entry.Value.ExpiresAt <= now))
            sources.TryRemove(entry.Key, out _);
    }
}
