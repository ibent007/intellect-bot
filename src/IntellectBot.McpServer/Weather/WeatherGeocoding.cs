using System.Globalization;
using System.Text.Json;
using IntellectBot.McpServer.Infrastructure;
using static IntellectBot.McpServer.Weather.WeatherDataParser;

namespace IntellectBot.McpServer.Weather;

// Разделяет поиск по имени и разрешение выбранного ID. Название и страна не угадываются.
internal sealed class WeatherGeocoding(SourceHttpClient wire, TimeProvider clock)
{
    private const string Endpoint = "https://geocoding-api.open-meteo.com/v1/";
    private readonly AsyncCache<string, Location[]> queries = new(clock);
    private readonly AsyncCache<int, Location> identities = new(clock);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    public async Task<Location[]> FindAsync(string query, int? selectedId, CancellationToken ct)
    {
        var parts = query.Split(',', StringSplitOptions.TrimEntries);
        var name = LocationNames.City(parts[0]);
        var country = parts.Length > 1 ? LocationNames.CountryCode(parts[1]) :
            LocationNames.Normalize(parts[0]) is "ПИТЕР" or "ПИТЕРЕ" or "СПБ" ? "RU" : null;
        if (selectedId is { } id)
        {
            var selected = await identities.GetAsync(id, Lifetime, async token =>
            {
                using var document = await wire.GetJsonAsync(Endpoint + "get?id=" + id.ToString(CultureInfo.InvariantCulture) + "&language=ru", token);
                var location = ParseLocation(document.RootElement);
                if (location.Public.Id != id)
                    throw InvalidResponse();
                return location;
            }, ct);
            return country is not null && selected.Public.CountryCode != country ? [] : [selected];
        }
        var key = LocationNames.Normalize(query);
        return await queries.GetAsync(key, Lifetime, async token =>
        {
            var qualifier = parts.Length > 1 ? ", " + parts[1] : "";
            using var document = await wire.GetJsonAsync(Endpoint + "search?name=" +
                Uri.EscapeDataString(name + qualifier) + "&count=20&language=ru&format=json" +
                (country is null ? "" : "&countryCode=" + country), token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
                throw InvalidResponse();
            if (!root.TryGetProperty("results", out var items))
                return [];
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100)
                throw InvalidResponse();
            var locations = items.EnumerateArray().Where(item => Text(item, "feature_code").StartsWith("PPL", StringComparison.Ordinal))
                .Select(ParseLocation).Where(value => country is null || value.Public.CountryCode == country)
                .Where(value => parts.Length < 3 || LocationNames.Normalize(value.Public.Admin1) == LocationNames.Normalize(parts[2]))
                .Where(value => parts.Length < 4 || LocationNames.Normalize(value.Public.Admin2) == LocationNames.Normalize(parts[3]))
                .DistinctBy(value => value.Public.Id).ToArray();
            // Сравниваем с локализованным запрошенным именем, не исключая Дубай из-за английских однофамильцев.
            var exact = locations.Where(value => LocationNames.Normalize(value.Public.Name) == LocationNames.Normalize(name)).ToArray();
            if (exact.Length > 0)
                locations = exact;
            locations = locations.DistinctBy(value => (LocationNames.Normalize(LocationNames.Label(value.Public)), value.Latitude, value.Longitude)).ToArray();
            var ranked = locations.OrderByDescending(value => value.Public.Population).ToArray();
            if (parts.Length < 3 && ranked.Length > 1 && ranked[0].Public.Population >= 50000 &&
                ranked[0].Public.PlaceType is "столица" or "административный центр" &&
                ranked.Skip(1).All(value => value.Public.Population < ranked[0].Public.Population / 20))
                locations = [ranked[0]];
            foreach (var location in locations)
                identities.Set(location.Public.Id, location, Lifetime);
            return locations;
        }, ct);
    }
}
