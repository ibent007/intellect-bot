using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntellectBot.McpServer.Infrastructure;

namespace IntellectBot.McpServer.Reference;

// Получает выдержку и постоянную ссылку. У модели нет права придумывать URL или дополнять цитату биографией.
public sealed class ReferenceService(HttpClient http, TimeProvider? clock = null)
{
    private const string Endpoint = "https://ru.wikipedia.org/w/api.php";
    private readonly SourceHttpClient wire = new(http);
    private readonly AsyncCache<string, ReferenceResult> cache = new(clock ?? TimeProvider.System, 128);

    public async Task<ReferenceResult> GetAsync(string topic, string? pageId = null, DateOnly? onDate = null, CancellationToken ct = default, string intent = "overview", string entityType = "any")
    {
        topic = topic?.Trim() ?? "";
        if (topic.Length is < 2 or > 150 || topic.Any(char.IsControl))
            throw new ArgumentException("Укажите имя или тему: от 2 до 150 символов.");
        if (intent is not ("overview" or "telegram") || entityType is not ("any" or "person"))
            throw new ArgumentException("Неподдерживаемый вид справочного запроса.");
        int? selected = null;
        if (pageId is not null)
        {
            if (!int.TryParse(pageId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new ArgumentException("Некорректный идентификатор статьи.");
            selected = id;
        }
        var date = onDate ?? DateOnly.FromDateTime((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
        var key = intent + ":" + entityType + ":" + topic.ToUpperInvariant() + ":" + selected + ":" + date.ToString("O", CultureInfo.InvariantCulture);
        var result = await cache.GetAsync(key, TimeSpan.FromMinutes(15), token => ReadAsync(topic, selected, date, intent, entityType, token), ct);
        if (result.Status == "unavailable")
            cache.Remove(key);
        return result;
    }

    private async Task<ReferenceResult> ReadAsync(string topic, int? selected, DateOnly date, string intent, string entityType, CancellationToken ct)
    {
        try
        {
            var selection = selected is { } id ? "pageids=" + id.ToString(CultureInfo.InvariantCulture) :
                "generator=search&gsrnamespace=0&gsrlimit=5&gsrsearch=" + Uri.EscapeDataString(topic);
            using var document = await wire.GetJsonAsync(Endpoint + "?action=query&format=json&formatversion=2&" + selection +
                "&prop=extracts%7Cinfo%7Cpageprops&exintro=1&explaintext=1&exchars=1200&exlimit=5&inprop=url", ct);
            var root = document.RootElement;
            if (!root.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var raw))
                return Missing();
            if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() > 5)
                throw new FormatException();
            var pages = raw.EnumerateArray().Where(page => !page.TryGetProperty("missing", out _))
                .Select(Parse).ToArray();
            if (pages.Length == 0)
                return Missing();
            if (selected is { } exactId && (pages.Length != 1 || pages[0].Id != exactId))
                throw new FormatException();
            if (selected is null)
            {
                var words = Words(topic);
                var exact = pages.Where(page => !page.Disambiguation && words.All(word => Words(page.Title).Contains(word))).ToArray();
                if (exact.Length > 0)
                    pages = exact;
                var exactTitle = pages.Where(page => words.SetEquals(Words(page.Title))).ToArray();
                if (exactTitle.Length == 1)
                    pages = exactTitle;
                if (entityType == "person")
                {
                    var people = pages.Where(page => !Regex.IsMatch(page.Title,
                        @"\((?:фильм|сериал|альбом|книга|игра)", RegexOptions.IgnoreCase)).ToArray();
                    if (people.Length > 0)
                        pages = people;
                }
            }
            if (pages.Length > 1 || pages[0].Disambiguation)
                return new("ambiguous", pages.Select(page => new ReferenceCandidate(page.Id, DisplayTitle(page.Title) + " — " + ShortDescription(page.Extract),
                    new Dictionary<string, string> { ["topic"] = page.Title, ["pageId"] = page.Id.ToString(CultureInfo.InvariantCulture), ["intent"] = intent, ["entityType"] = entityType })).ToArray(), "");
            var chosen = pages[0];
            var ageText = intent == "overview" ? await AgeAsync(chosen.Entity, date, ct) : "";
            var url = "https://ru.wikipedia.org/w/index.php?oldid=" + chosen.Revision.ToString(CultureInfo.InvariantCulture);
            var title = DisplayTitle(chosen.Title);
            if (intent == "telegram")
            {
                var channel = await TelegramAsync(chosen.Entity, ct);
                return new("ok", [], title + "\n\n" + channel, title, url, chosen.Revision);
            }
            var facts = Sentences(chosen.Extract).Concat(string.IsNullOrWhiteSpace(ageText)
                ? [] : new[] { ageText.Trim() }).Take(14).ToArray();
            var source = "\n\nИсточник: " + url + "\nВикипедия · CC BY-SA.";
            return new("ok", [], title + "\n\nКратко\n" + string.Join("\n\n", facts) + source,
                title, url, chosen.Revision, Facts: facts);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Unavailable(); }
        catch (Exception error) when (error is SourceException or HttpRequestException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or IOException)
        {
            return Unavailable();
        }
    }

    private async Task<string> TelegramAsync(string? entity, CancellationToken ct)
    {
        if (entity is null || !Regex.IsMatch(entity, @"^Q[1-9][0-9]{0,12}$"))
            return "Источник не содержит подтверждённой ссылки на Telegram-канал. Не буду угадывать адрес.";
        using var data = await wire.GetJsonAsync("https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=claims&ids=" + entity, ct);
        var claims = data.RootElement.GetProperty("entities").GetProperty(entity).GetProperty("claims");
        if (!claims.TryGetProperty("P3789", out var entries))
            return "Источник не содержит подтверждённой ссылки на Telegram-канал. Не буду угадывать адрес.";
        var usernames = entries.EnumerateArray().Where(item => !item.TryGetProperty("rank", out var rank) || rank.GetString() != "deprecated")
            .Select(item => item.TryGetProperty("mainsnak", out var snak) && snak.TryGetProperty("datavalue", out var value) &&
                value.TryGetProperty("value", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null)
            .Where(name => name is not null && Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]{3,31}$"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return usernames.Length == 0
            ? "Источник не содержит подтверждённой ссылки на Telegram-канал. Не буду угадывать адрес."
            : "Telegram: " + string.Join("\n", usernames.Select(name => "https://t.me/" + name)) +
                "\n\nИсточник: https://www.wikidata.org/wiki/" + entity;
    }

    private static string DisplayTitle(string title)
    {
        var parts = title.Split(", ", 2);
        return parts.Length == 2 ? parts[1] + " " + parts[0] : title;
    }

    private static string ShortDescription(string extract)
    {
        var text = extract.Replace("\u0301", "");
        var dash = text.IndexOf(" — ", StringComparison.Ordinal);
        if (dash >= 0)
            text = text[(dash + 3)..];
        return text.Length > 85 ? text[..82] + "…" : text;
    }

    private static IEnumerable<string> Sentences(string text)
    {
        // Убираем только обрезанный хвост API. Не дописываем отсутствующие сведения.
        var clean = text.Replace("\u0301", "").Trim();
        if (clean.EndsWith("...", StringComparison.Ordinal))
        {
            var end = clean[..^3].LastIndexOf('.');
            clean = end < 0 ? "" : clean[..(end + 1)];
        }
        return Regex.Split(clean, @"(?<=[.!?])\s+(?=[А-ЯЁ«])").Where(part => part.Length > 0);
    }

    private async Task<string> AgeAsync(string? entity, DateOnly date, CancellationToken ct)
    {
        if (entity is null || !Regex.IsMatch(entity, @"^Q[1-9][0-9]{0,12}$"))
            return "";
        try
        {
            using var data = await wire.GetJsonAsync("https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=claims&ids=" + entity, ct);
            var claims = data.RootElement.GetProperty("entities").GetProperty(entity).GetProperty("claims");
            // Для умершего человека текущий возраст не рассчитывается.
            if (claims.TryGetProperty("P570", out _))
                return "";
            var dates = claims.GetProperty("P569").EnumerateArray().Where(item =>
                !item.TryGetProperty("rank", out var rank) || rank.GetString() != "deprecated")
                .Select(item => item.GetProperty("mainsnak").GetProperty("datavalue").GetProperty("value"))
                .Where(value => value.GetProperty("precision").GetInt32() >= 11 &&
                    value.GetProperty("calendarmodel").GetString() == "http://www.wikidata.org/entity/Q1985727")
                .Select(value => value.GetProperty("time").GetString()!).Distinct().ToArray();
            if (dates.Length != 1 || dates[0].Length < 11 || !dates[0].StartsWith('+') ||
                !DateOnly.TryParseExact(dates[0].Substring(1, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var birth) || birth > date)
                return "";
            var age = date.Year - birth.Year - (date < birth.AddYears(date.Year - birth.Year) ? 1 : 0);
            return $"\n\nДата рождения: {birth:dd.MM.yyyy}. Возраст на {date:dd.MM.yyyy}: {age}." +
                "\nДанные о рождении: https://www.wikidata.org/wiki/" + entity;
        }
        catch (Exception error) when (error is SourceException or HttpRequestException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OperationCanceledException or IOException)
        {
            return "";
        }
    }

    private sealed record Page(int Id, string Title, string Extract, long Revision, string? Entity, bool Disambiguation);
    private static Page Parse(JsonElement page)
    {
        var id = page.GetProperty("pageid").GetInt32();
        var title = page.GetProperty("title").GetString()!;
        var extract = page.GetProperty("extract").GetString()!;
        var revision = page.GetProperty("lastrevid").GetInt64();
        if (id <= 0 || revision <= 0 || title.Length is < 1 or > 200 || extract.Length is < 1 or > 5000)
            throw new FormatException();
        page.TryGetProperty("pageprops", out var props);
        var entity = props.ValueKind == JsonValueKind.Object && props.TryGetProperty("wikibase_item", out var item) ? item.GetString() : null;
        return new(id, title, extract, revision, entity, props.ValueKind == JsonValueKind.Object && props.TryGetProperty("disambiguation", out _));
    }
    private static HashSet<string> Words(string value) => Regex.Matches(value.ToLowerInvariant().Replace('ё', 'е'), @"\p{L}+")
        .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
    private static ReferenceResult Missing() => new("not_found", [], "Источник не нашёл подходящей статьи. Уточните полное имя или название темы.");
    private static ReferenceResult Unavailable() => new("unavailable", [], "Не удалось проверить сведения по источнику. Попробуйте позже; неподтверждённые факты не приводятся.");
}
