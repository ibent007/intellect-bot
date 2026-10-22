using System.Text.Json.Serialization;

namespace IntellectBot.McpServer.Weather;

[JsonConverter(typeof(JsonStringEnumConverter<WeatherStatus>))]
public enum WeatherStatus
{
    [JsonStringEnumMemberName("ok")]
    Ok,

    [JsonStringEnumMemberName("not_found")]
    NotFound,

    [JsonStringEnumMemberName("ambiguous")]
    Ambiguous
}

public sealed record WeatherLocation(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("country_code")] string CountryCode,
    [property: JsonPropertyName("admin1")] string Admin1,
    [property: JsonPropertyName("admin2")] string Admin2,
    [property: JsonPropertyName("latitude")] double? Latitude = null,
    [property: JsonPropertyName("longitude")] double? Longitude = null,
    [property: JsonPropertyName("admin3")] string Admin3 = "",
    [property: JsonPropertyName("admin4")] string Admin4 = "",
    [property: JsonPropertyName("place_type")] string PlaceType = "населённый пункт",
    [property: JsonPropertyName("display_name")] string DisplayName = "",
    [property: JsonPropertyName("selection_arguments")] IReadOnlyDictionary<string, string>? SelectionArguments = null,
    [property: JsonPropertyName("population")] long Population = 0);

public sealed record WeatherData(
    [property: JsonPropertyName("location")] WeatherLocation Location,
    [property: JsonPropertyName("temperature_c")] double TemperatureC,
    [property: JsonPropertyName("temperature_unit")] string TemperatureUnit,
    [property: JsonPropertyName("weather_code")] int WeatherCode,
    [property: JsonPropertyName("wind_speed")] double WindSpeed,
    [property: JsonPropertyName("wind_unit")] string WindUnit,
    [property: JsonPropertyName("observed_at")] string ObservedAt,
    [property: JsonPropertyName("timezone")] string TimeZone,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("data_kind")] string DataKind,
    [property: JsonPropertyName("conditions")] string Conditions = "");

public sealed record WeatherResult(
    [property: JsonPropertyName("status")] WeatherStatus Status,
    [property: JsonPropertyName("candidates")] WeatherLocation[] Candidates,
    [property: JsonPropertyName("weather"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] WeatherData? Weather,
    [property: JsonPropertyName("total_candidates")] int TotalCandidates = 0,
    [property: JsonPropertyName("candidates_truncated")] bool CandidatesTruncated = false,
    [property: JsonPropertyName("public_text")] string PublicText = "",
    [property: JsonPropertyName("evidence_kind")] string EvidenceKind = "weather");

public sealed class WeatherException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}
