using System.Text;
using System.Text.RegularExpressions;

namespace IntellectBot.McpServer.Weather;

// Словарь написаний, а не база городов: координаты и страны всегда проверяются источником.
internal static class LocationNames
{
    public static string Normalize(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC)
        .Replace('‑', '-').Replace('–', '-').Replace('—', '-').Replace('ё', 'е').Trim(), @"\s+", " ").ToUpperInvariant();

    public static string City(string name) => Normalize(name) switch
    {
        "DUBAI" or "ДУБАЕ" or "ДУБАЯ" or "ДУБАИ" => "Дубай",
        "ПИТЕР" or "ПИТЕРЕ" or "СПБ" or "ПЕТЕРБУРГ" or "SAINT PETERSBURG" or "ST PETERSBURG" or "SAINT-PETERSBURG" => "Санкт-Петербург",
        "MOSCOW" or "МОСКВЕ" or "МОСКВЫ" => "Москва",
        "LONDON" or "ЛОНДОНЕ" => "Лондон",
        "BERLIN" or "БЕРЛИНЕ" => "Берлин",
        "RIGA" or "РИГЕ" => "Рига",
        "КОТЛАСЕ" => "Котлас",
        "КРАСНОДАРЕ" => "Краснодар",
        "ТОМСКЕ" => "Томск",
        "САМАРЕ" => "Самара",
        "МИЧУРИНСКЕ" => "Мичуринск",
        _ => name.Trim().Replace('‑', '-')
    };

    public static string? CountryCode(string country) => Normalize(country) switch
    {
        "РОССИЯ" or "РОССИИ" or "РФ" or "RUSSIA" => "RU",
        "ОАЭ" or "ОБЪЕДИНЕННЫЕ АРАБСКИЕ ЭМИРАТЫ" or "UAE" or "UNITED ARAB EMIRATES" => "AE",
        "США" or "СОЕДИНЕННЫЕ ШТАТЫ" or "USA" or "UNITED STATES" => "US",
        "ВЕЛИКОБРИТАНИЯ" or "БРИТАНИЯ" or "UK" or "UNITED KINGDOM" => "GB",
        "ЛАТВИЯ" or "ЛАТВИИ" or "LATVIA" => "LV",
        "ГЕРМАНИЯ" or "GERMANY" => "DE",
        "БЕЛАРУСЬ" or "БЕЛОРУССИЯ" or "BELARUS" => "BY",
        "ГРУЗИЯ" or "GEORGIA" => "GE",
        "КАЗАХСТАН" or "KAZAKHSTAN" => "KZ",
        "КИТАЙ" or "CHINA" => "CN",
        var code when code.Length == 2 && code.All(char.IsAsciiLetter) => code,
        _ => null
    };

    public static string Label(WeatherLocation value) => string.Join(", ",
        new[] { value.Name, value.Country, value.Admin1, value.Admin2, value.Admin3, value.Admin4 }
            .Where(text => !string.IsNullOrWhiteSpace(text)).DistinctBy(Normalize));
}
