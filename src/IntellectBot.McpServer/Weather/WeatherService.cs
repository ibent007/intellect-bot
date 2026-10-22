using System.Globalization;
using System.Text;
using System.Text.Json;
using IntellectBot.McpServer.Infrastructure;
using static IntellectBot.McpServer.Weather.WeatherDataParser;

namespace IntellectBot.McpServer.Weather;

public sealed class WeatherService(HttpClient http, TimeProvider? clock = null)
{

    private const string WeatherEndpoint = "https://api.open-meteo.com/v1/forecast";

    private readonly SourceHttpClient wire = new(http);
    private readonly WeatherGeocoding geocoding = new(new SourceHttpClient(http), clock ?? TimeProvider.System);
    private readonly AsyncCache<int, WeatherResult> readings = new(clock ?? TimeProvider.System);

    public async Task<WeatherResult> GetAsync(
        string city,
        CancellationToken cancellationToken = default,
        string? locationId = null)
    {
        int? selectedId = null;

        if (locationId is not null)
        {
            if (!int.TryParse(
                    locationId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var id) ||
                id <= 0)
            {
                throw new ArgumentException("Некорректный ID места.");
            }

            selectedId = id;
        }

        var query = city?.Trim() ?? "";

        if (query.EnumerateRunes().Count() is < 1 or > 100)
            throw new ArgumentException(
                "Город: требуется от 1 до 100 символов.");

        var parts = query.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length > 4 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(
                "Используйте: город[, страна[, регион[, район]]].");

        try
        {
            var locations = await geocoding.FindAsync(query, selectedId, cancellationToken);
            if (locations.Length == 0)
                return new(WeatherStatus.NotFound, [], null, PublicText: "Источник не нашёл этот город. Уточните название и страну.");
            if (locations.Length > 1)
            {
                var candidates = locations.Take(10).Select(value => value.Public with
                {
                    DisplayName = LocationNames.Label(value.Public),
                    SelectionArguments = new Dictionary<string, string>
                    {
                        ["city"] = value.Public.Name + ", " + value.Public.CountryCode,
                        ["locationId"] = value.Public.Id.ToString(CultureInfo.InvariantCulture)
                    }
                }).ToArray();
                return new(WeatherStatus.Ambiguous, candidates, null, locations.Length, locations.Length > 10);
            }
            var selected = locations[0];
            return await readings.GetAsync(selected.Public.Id, TimeSpan.FromMinutes(2),
                token => ReadAsync(selected, token), cancellationToken);
        }
        catch (SourceException error) { throw new WeatherException(error.Code, error.Message); }
        catch (WeatherException) { throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WeatherException("timeout", "Погодный сервис не ответил вовремя.");
        }
        catch (HttpRequestException)
        {
            throw new WeatherException("unavailable", "Погодный сервис временно недоступен.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw InvalidResponse();
        }
    }

    private async Task<WeatherResult> ReadAsync(Location selected, CancellationToken cancellationToken)
    {
        try
        {
            var weatherUrl =
                WeatherEndpoint +
                "?latitude=" +
                selected.Latitude.ToString(CultureInfo.InvariantCulture) +
                "&longitude=" +
                selected.Longitude.ToString(CultureInfo.InvariantCulture) +
                "&current=temperature_2m,weather_code,wind_speed_10m" +
                "&temperature_unit=celsius&wind_speed_unit=ms" +
                "&timeformat=unixtime&forecast_days=1" +
                "&timezone=" + Uri.EscapeDataString(selected.TimeZone);

            using var weatherDocument = await wire.GetJsonAsync(
                weatherUrl, cancellationToken);

            var weatherRoot = weatherDocument.RootElement;
            var current = weatherRoot.GetProperty("current");
            var units = weatherRoot.GetProperty("current_units");

            if (weatherRoot.TryGetProperty("error", out _) ||
                Text(units, "temperature_2m") != "°C" ||
                Text(units, "wind_speed_10m") != "m/s" ||
                Text(units, "time") != "unixtime")
            {
                throw InvalidResponse();
            }

            var zoneName = Text(weatherRoot, "timezone");

            if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(zoneName, out _))
                throw InvalidResponse();

            var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName);
            var temperature = Number(current, "temperature_2m");
            var wind = Number(current, "wind_speed_10m");
            var code = current.GetProperty("weather_code").GetInt32();

            if (temperature is < -100 or > 70 ||
                wind is < 0 or > 150 ||
                code is not (
                    0 or 1 or 2 or 3 or 45 or 48 or
                    51 or 53 or 55 or 56 or 57 or
                    61 or 63 or 65 or 66 or 67 or
                    71 or 73 or 75 or 77 or
                    80 or 81 or 82 or 85 or 86 or
                    95 or 96 or 97 or 99))
            {
                throw InvalidResponse();
            }

            var instant = DateTimeOffset.FromUnixTimeSeconds(
                current.GetProperty("time").GetInt64());

            var localTime = TimeZoneInfo.ConvertTime(instant, zone);

            var weather = new WeatherData(
                selected.Public,
                temperature,
                "°C",
                code,
                wind,
                "m/s",
                localTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:sszzz",
                    CultureInfo.InvariantCulture),
                zoneName,
                "Open-Meteo",
                "model_estimate", WeatherConditions.Describe(code));

            var temperatureText = Math.Round(temperature, 1) == 0 ? "0" : temperature.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"));
            var publicText = $"Погода: {LocationNames.Label(selected.Public)}\n\n" +
                $"Температура: {temperatureText} °C\n" +
                $"Сейчас: {weather.Conditions}\n" +
                $"Ветер: {wind.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"))} м/с\n\n" +
                $"Данные на {localTime:dd.MM.yyyy HH:mm} по времени города.\nИсточник: Open-Meteo.";
            return new WeatherResult(WeatherStatus.Ok, [], weather, PublicText: publicText);
        }
        catch (SourceException error) { throw new WeatherException(error.Code, error.Message); }
        catch (WeatherException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new WeatherException(
                "timeout", "Погодный сервис не ответил вовремя.");
        }
        catch (HttpRequestException)
        {
            throw new WeatherException(
                "unavailable", "Погодный сервис временно недоступен.");
        }
        catch (Exception exception) when (
            exception is JsonException or
            KeyNotFoundException or
            InvalidOperationException or
            FormatException or
            ArgumentException or
            TimeZoneNotFoundException or
            InvalidTimeZoneException)
        {
            throw InvalidResponse();
        }
    }

}
