using System.Text.Json;

namespace IntellectBot.McpServer.Weather;

// Проверка полей Open-Meteo и нормализация локализованных названий.
internal static class WeatherDataParser
{
    internal static Location ParseLocation(JsonElement item)
    {
        var id = item.GetProperty("id").GetInt32();
        var name = Text(item, "name");
        var countryCode = Text(item, "country_code");
        var country = OptionalText(item, "country");

        if (string.IsNullOrWhiteSpace(country) || !country.Any(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё'))
            country = RussianCountry(countryCode);
        var zone = Text(item, "timezone");
        var latitude = Number(item, "latitude");
        var longitude = Number(item, "longitude");

        if (id <= 0 ||
            countryCode.Length != 2 ||
            countryCode.Any(character => character is < 'A' or > 'Z') ||
            latitude is < -90 or > 90 ||
            longitude is < -180 or > 180 ||
            !TimeZoneInfo.TryConvertIanaIdToWindowsId(zone, out _))
        {
            throw InvalidResponse();
        }

        return new Location(
            new WeatherLocation(
                id, name, country, countryCode,
                RussianRegion(OptionalText(item, "admin1")),
                RussianRegion(OptionalText(item, "admin2")), latitude, longitude,
                RussianRegion(OptionalText(item, "admin3")), RussianRegion(OptionalText(item, "admin4")),
                PlaceType(OptionalText(item, "feature_code")), Population:
                    item.TryGetProperty("population", out var population) && population.TryGetInt64(out var count) ? Math.Max(0, count) : 0),
            latitude,
            longitude,
            zone);
    }

    internal static string RussianCountry(string code) => code switch
    {
        "MS" => "Монтсеррат",
        "RU" => "Россия",
        "US" => "США",
        "AE" => "ОАЭ",
        "GB" => "Великобритания",
        "LV" => "Латвия",
        "BY" => "Беларусь",
        "KZ" => "Казахстан",
        "UA" => "Украина",
        "DE" => "Германия",
        "FR" => "Франция",
        "IT" => "Италия",
        "ES" => "Испания",
        "CN" => "Китай",
        "JP" => "Япония",
        "CA" => "Канада",
        "AU" => "Австралия",
        "IN" => "Индия",
        "TJ" => "Таджикистан",
        "TM" => "Туркменистан",
        "KI" => "Кирибати",
        _ => "Территория " + code
    };

    internal static string PlaceType(string feature) => feature switch
    {
        "PPLC" => "столица",
        "PPLA" or "PPLA2" or "PPLA3" or "PPLA4" => "административный центр",
        "PPLX" => "район населённого пункта",
        _ => "населённый пункт"
    };

    internal static string Text(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse($"поле «{name}» отсутствует или не является строкой");
        }

        var value = property.GetString();

        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
            throw InvalidResponse($"поле «{name}» пустое или слишком длинное");

        return value;
    }

    internal static string OptionalText(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return "";
        }

        var value = property.GetString();

        if (value is null || value.Length > 200)
            throw InvalidResponse($"некорректное дополнительное поле «{name}»");

        return value;
    }

    internal static double Number(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out var value) ||
            !double.IsFinite(value))
        {
            throw InvalidResponse($"поле «{name}» отсутствует или не является числом");
        }

        return value;
    }

    internal static WeatherException InvalidResponse(
        string reason = "структура ответа или значения не прошли проверку") =>
        new("invalid_response", $"Погодный сервис вернул некорректные данные: {reason}.");

    internal sealed record Location(
        WeatherLocation Public,
        double Latitude,
        double Longitude,
        string TimeZone);

    internal static string RussianRegion(string value)
    {
        var normalized = value.Trim()
            .Replace('’', '\'')
            .TrimEnd('\'')
            .ToLowerInvariant();

        return normalized switch
        {
            "zapadno-kazakhstanskaya oblast" =>
                "Западно-Казахстанская область",
            "gorod sevastopol" => "Севастополь",
            "dubai" => "Дубай",
            "greater london" => "Большой Лондон",
            "england" => "Англия",
            "line islands" => "Острова Лайн",
            "kiritimati" => "Киритимати",
            _ => value.Any(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё') ? value : ""
        };
    }
}
