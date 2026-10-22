using System.Net;
using IntellectBot.McpServer.Weather;
using Xunit;

namespace IntellectBot.Tests;

public sealed class WeatherTests
{
    private const string Berlin = """
        {
          "id": 2950159,
          "name": "Берлин",
          "feature_code": "PPLC",
          "country": "Германия",
          "country_code": "DE",
          "admin1": "Берлин",
          "admin2": "Берлин",
          "latitude": 52.52,
          "longitude": 13.41,
          "timezone": "Europe/Berlin"
        }
        """;

    private const string GoodWeather = """
        {
          "timezone": "Europe/Berlin",
          "current_units": {
            "time": "unixtime",
            "temperature_2m": "°C",
            "wind_speed_10m": "m/s"
          },
          "current": {
            "time": 1767225600,
            "temperature_2m": 5.5,
            "weather_code": 3,
            "wind_speed_10m": 2.7
          }
        }
        """;

    [Fact]
    public async Task SuccessReturnsExactFieldsAndFixedUrls()
    {
        var calls = 0;

        using var http = new HttpClient(new StubHttp((request, _) =>
        {
            calls++;
            var uri = request.RequestUri!;

            Assert.Equal("https", uri.Scheme);

            if (calls == 1)
            {
                Assert.Equal(
                    "geocoding-api.open-meteo.com", uri.Host);
                Assert.Equal("/v1/search", uri.AbsolutePath);
                Assert.Contains(
                    "name=Берлин",
                    Uri.UnescapeDataString(uri.Query));

                return Task.FromResult(
                    StubHttp.Json(Geo(Berlin)));
            }

            Assert.Equal("api.open-meteo.com", uri.Host);
            Assert.Equal("/v1/forecast", uri.AbsolutePath);
            Assert.Contains("latitude=52.52", uri.Query);
            Assert.Contains("longitude=13.41", uri.Query);
            Assert.Contains("wind_speed_unit=ms", uri.Query);
            Assert.Contains("timeformat=unixtime", uri.Query);

            return Task.FromResult(StubHttp.Json(GoodWeather));
        }));

        var result = await new WeatherService(http)
            .GetAsync("  Берлин  ");

        Assert.Equal(2, calls);
        Assert.Equal(WeatherStatus.Ok, result.Status);
        Assert.Empty(result.Candidates);

        var data = Assert.IsType<WeatherData>(result.Weather);

        Assert.Equal(2950159, data.Location.Id);
        Assert.Equal("Берлин", data.Location.Name);
        Assert.Equal("Германия", data.Location.Country);
        Assert.Equal("DE", data.Location.CountryCode);
        Assert.Equal("Берлин", data.Location.Admin1);
        Assert.Equal(5.5, data.TemperatureC);
        Assert.Equal("°C", data.TemperatureUnit);
        Assert.Equal(3, data.WeatherCode);
        Assert.Equal(2.7, data.WindSpeed);
        Assert.Equal("m/s", data.WindUnit);
        Assert.Equal(
            "2026-01-01T01:00:00+01:00", data.ObservedAt);
        Assert.Equal("Europe/Berlin", data.TimeZone);
        Assert.Equal("Open-Meteo", data.Source);
        Assert.Equal("model_estimate", data.DataKind);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"results":[]}""")]
    public async Task MissingCityDoesNotRequestWeather(string json)
    {
        var calls = 0;

        using var http = new HttpClient(new StubHttp((_, _) =>
        {
            calls++;
            return Task.FromResult(StubHttp.Json(json));
        }));

        var result = await new WeatherService(http)
            .GetAsync("Несуществующий город");

        Assert.Equal(1, calls);
        Assert.Equal(WeatherStatus.NotFound, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Null(result.Weather);
    }

    [Fact]
    public async Task AmbiguousCityReturnsDistinctRegions()
    {
        var other = Berlin
            .Replace("2950159", "123456")
            .Replace(
                "\"admin1\": \"Берлин\"",
                "\"admin1\": \"Другой регион\"");

        var calls = 0;

        using var http = new HttpClient(new StubHttp((_, _) =>
        {
            calls++;
            return Task.FromResult(
                StubHttp.Json(Geo(Berlin, other)));
        }));

        var result = await new WeatherService(http).GetAsync("Берлин");

        Assert.Equal(1, calls);
        Assert.Equal(WeatherStatus.Ambiguous, result.Status);
        Assert.Null(result.Weather);
        Assert.Equal(2, result.Candidates.Length);
        Assert.Equal("Германия", result.Candidates[0].Country);
        Assert.Equal("Берлин", result.Candidates[0].Admin1);
        Assert.Equal("Другой регион", result.Candidates[1].Admin1);
    }

    [Fact]
    public async Task RegionQualifierSelectsOnlyMatchingLocation()
    {
        var other = Berlin
            .Replace("2950159", "123456")
            .Replace(
                "\"admin1\": \"Берлин\"",
                "\"admin1\": \"Другой регион\"");

        var calls = 0;

        using var http = new HttpClient(new StubHttp((request, _) =>
        {
            calls++;

            if (calls == 1)
            {
                var query = Uri.UnescapeDataString(
                    request.RequestUri!.Query);

                Assert.Contains("name=Берлин, DE", query);
                Assert.DoesNotContain("Другой регион", query);

                return Task.FromResult(
                    StubHttp.Json(Geo(Berlin, other)));
            }

            return Task.FromResult(StubHttp.Json(GoodWeather));
        }));

        var result = await new WeatherService(http)
            .GetAsync("Берлин, DE, Другой регион");

        Assert.Equal(2, calls);
        Assert.Equal(WeatherStatus.Ok, result.Status);
        Assert.Equal(
            123456,
            Assert.IsType<WeatherData>(result.Weather).Location.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Берлин,")]
    [InlineData("a,b,c,d,e")]
    public async Task InvalidInputMakesNoHttpCalls(string city)
    {
        using var http = new HttpClient(new StubHttp((_, _) =>
            throw new InvalidOperationException("HTTP запрещён.")));

        await Assert.ThrowsAsync<ArgumentException>(
            () => new WeatherService(http).GetAsync(city));
    }

    [Fact]
    public async Task TooLongInputIsRejected()
    {
        using var http = new HttpClient(new StubHttp((_, _) =>
            throw new InvalidOperationException("HTTP запрещён.")));

        await Assert.ThrowsAsync<ArgumentException>(
            () => new WeatherService(http).GetAsync(new string('a', 101)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAtEitherStageIsSafe(bool duringWeather)
    {
        var calls = 0;

        using var http = new HttpClient(new StubHttp((_, _) =>
        {
            calls++;

            if (!duringWeather || calls == 2)
                throw new TaskCanceledException("secret diagnostic");

            return Task.FromResult(StubHttp.Json(Geo(Berlin)));
        }));

        var error = await Assert.ThrowsAsync<WeatherException>(
            () => new WeatherService(http).GetAsync("Берлин"));

        Assert.Equal("timeout", error.Code);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Equal(duringWeather ? 2 : 1, calls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task HttpFailureIsSafeAndNotRetried(int status)
    {
        var calls = 0;

        using var http = new HttpClient(new StubHttp((_, _) =>
        {
            calls++;

            return Task.FromResult(StubHttp.Json(
                "secret body", (HttpStatusCode)status));
        }));

        var error = await Assert.ThrowsAsync<WeatherException>(
            () => new WeatherService(http).GetAsync("Берлин"));

        Assert.Equal("unavailable", error.Code);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("""{"results":null}""")]
    [InlineData("""{"results":[{}]}""")]
    public async Task InvalidGeocodingIsRejected(string json)
    {
        using var http = new HttpClient(new StubHttp((_, _) =>
            Task.FromResult(StubHttp.Json(json))));

        var error = await Assert.ThrowsAsync<WeatherException>(
            () => new WeatherService(http).GetAsync("Берлин"));

        Assert.Equal("invalid_response", error.Code);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("""{"current":null}""")]
    public async Task InvalidWeatherIsRejected(string json)
    {
        var calls = 0;

        using var http = new HttpClient(new StubHttp((_, _) =>
            Task.FromResult(StubHttp.Json(
                ++calls == 1 ? Geo(Berlin) : json))));

        var error = await Assert.ThrowsAsync<WeatherException>(
            () => new WeatherService(http).GetAsync("Берлин"));

        Assert.Equal("invalid_response", error.Code);
    }

    [Fact]
    public async Task WrongUnitsAreRejected()
    {
        var calls = 0;

        using var http = new HttpClient(new StubHttp((_, _) =>
            Task.FromResult(StubHttp.Json(
                ++calls == 1
                    ? Geo(Berlin)
                    : GoodWeather.Replace("\"m/s\"", "\"km/h\"")))));

        var error = await Assert.ThrowsAsync<WeatherException>(
            () => new WeatherService(http).GetAsync("Берлин"));

        Assert.Equal("invalid_response", error.Code);
    }

    [Fact]
    public async Task NetworkFailureIsSafe()
    {
        using var http = new HttpClient(new StubHttp((_, _) =>
            throw new HttpRequestException("secret diagnostic")));

        var error = await Assert.ThrowsAsync<WeatherException>(
            () => new WeatherService(http).GetAsync("Берлин"));

        Assert.Equal("unavailable", error.Code);
        Assert.DoesNotContain("secret", error.Message);
    }

    private static string Geo(params string[] locations) =>
        "{\"results\":[" + string.Join(",", locations) + "]}";

    [Fact]
    public async Task ExactCityNameExcludesOtherNamesWithSamePrefix()
    {
        var neighbour = Berlin
            .Replace("2950159", "123456")
            .Replace(
                "\"name\": \"Берлин\"",
                "\"name\": \"Берлинский район\"");

        var calls = 0;

        using var http = new HttpClient(new StubHttp((_, _) =>
            Task.FromResult(StubHttp.Json(
                ++calls == 1
                    ? Geo(neighbour, Berlin)
                    : GoodWeather))));

        var result = await new WeatherService(http)
            .GetAsync("Берлин, DE, Берлин, Берлин");

        Assert.Equal(WeatherStatus.Ok, result.Status);
        Assert.Equal(2950159, result.Weather!.Location.Id);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SelectedIdDistinguishesLocationsWithSameVisibleName()
    {
        var other = Berlin.Replace("2950159", "123456");
        var calls = 0;

        using var http = new HttpClient(new StubHttp((_, _) =>
            Task.FromResult(StubHttp.Json(
                ++calls == 1 ? other : GoodWeather))));

        var result = await new WeatherService(http).GetAsync(
            "Берлин, DE, Берлин, Берлин",
            default,
            "123456");

        Assert.Equal(WeatherStatus.Ok, result.Status);
        Assert.Equal(123456, result.Weather!.Location.Id);
    }
    [Fact]
    public async Task DubaiUsesLocalizedRequestedNameInsteadOfUnrelatedEnglishNames()
    {
        var uae = Berlin.Replace("2950159", "292223").Replace("Берлин", "Дубай").Replace("Германия", "ОАЭ").Replace("DE", "AE");
        var india = Berlin.Replace("2950159", "123456").Replace("Берлин", "Dubai").Replace("Германия", "Индия").Replace("DE", "IN");
        var calls = 0;
        using var http = new HttpClient(new StubHttp((request, _) =>
        {
            calls++;
            if (calls == 1)
                Assert.Contains("name=Дубай", Uri.UnescapeDataString(request.RequestUri!.Query));
            return Task.FromResult(StubHttp.Json(calls == 1 ? Geo(india, uae) : GoodWeather));
        }));
        var result = await new WeatherService(http).GetAsync("Dubai");
        Assert.Equal(WeatherStatus.Ok, result.Status);
        Assert.Equal("AE", result.Weather!.Location.CountryCode);
        Assert.Contains("Дубай, ОАЭ", result.PublicText);
        Assert.DoesNotContain("Индия", result.PublicText);
    }

    [Fact]
    public async Task WarmReadingAndSelectedIdDoNotRepeatHttpSearch()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttp((_, _) =>
            Task.FromResult(StubHttp.Json(++calls == 1 ? Geo(Berlin) : GoodWeather))));
        var service = new WeatherService(http);
        var first = await service.GetAsync("Берлин");
        var again = await service.GetAsync("Берлин");
        var selected = await service.GetAsync("Берлин, DE", default, "2950159");
        Assert.Equal(2, calls);
        Assert.Same(first, again);
        Assert.Same(first, selected);
        Assert.Contains("Температура: 5,5 °C\nСейчас: пасмурно\nВетер: 2,7 м/с", selected.PublicText);
        Assert.DoesNotContain("2950159", selected.PublicText);
    }

    [Fact]
    public async Task FailedSourceReadCanBeRetriedWithoutPoisoningCache()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttp((_, _) =>
        {
            var call = ++calls;
            if (call == 2)
                throw new HttpRequestException("Источник недоступен");
            return Task.FromResult(StubHttp.Json(call == 1 ? Geo(Berlin) : GoodWeather));
        }));
        var service = new WeatherService(http);
        await Assert.ThrowsAsync<WeatherException>(() => service.GetAsync("Берлин"));
        Assert.Equal(WeatherStatus.Ok, (await service.GetAsync("Берлин")).Status);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ConcurrentReadsOfSameCityShareSourceRequests()
    {
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new StubHttp(async (_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            await gate.Task;
            return StubHttp.Json(call == 1 ? Geo(Berlin) : GoodWeather);
        }));
        var service = new WeatherService(http);
        var first = service.GetAsync("Берлин");
        var second = service.GetAsync("Берлин");
        gate.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(2, calls);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task PiterAliasSearchesSaintPetersburg()
    {
        using var http = new HttpClient(new StubHttp((request, _) =>
        {
            Assert.Contains("name=Санкт-Петербург", Uri.UnescapeDataString(request.RequestUri!.Query));
            return Task.FromResult(StubHttp.Json("{}"));
        }));
        Assert.Equal(WeatherStatus.NotFound, (await new WeatherService(http).GetAsync("Питер")).Status);
    }

    [Fact]
    public async Task DominantAdministrativeCityDoesNotAskAboutTinyHomonyms()
    {
        var city = Berlin.Replace("\"feature_code\": \"PPLC\"", "\"feature_code\": \"PPLC\", \"population\": 1000000");
        var village = Berlin.Replace("2950159", "111").Replace("\"feature_code\": \"PPLC\"", "\"feature_code\": \"PPL\", \"population\": 100");
        var calls = 0;
        using var http = new HttpClient(new StubHttp((_, _) => Task.FromResult(StubHttp.Json(
            ++calls == 1 ? Geo(city, village) : GoodWeather))));
        var result = await new WeatherService(http).GetAsync("Берлин");
        Assert.Equal(WeatherStatus.Ok, result.Status);
        Assert.Equal(2950159, result.Weather!.Location.Id);
    }

    [Fact]
    public async Task ComparableCitiesStillRequireSelection()
    {
        var city = Berlin.Replace("\"feature_code\": \"PPLC\"", "\"feature_code\": \"PPLC\", \"population\": 1000000");
        var other = city.Replace("2950159", "111").Replace("1000000", "900000").Replace("52.52", "51.52");
        using var http = new HttpClient(new StubHttp((_, _) => Task.FromResult(StubHttp.Json(Geo(city, other)))));
        Assert.Equal(WeatherStatus.Ambiguous, (await new WeatherService(http).GetAsync("Берлин")).Status);
    }

    [Fact]
    public async Task MissingRussianCountryIsLocalizedAndUntranslatedRegionIsNotDisplayed()
    {
        var place = Berlin.Replace("Германия", "Montserrat").Replace("\"DE\"", "\"MS\"")
            .Replace("\"admin1\": \"Берлин\"", "\"admin1\": \"Parish of Saint Peter\"");
        var calls = 0;
        using var http = new HttpClient(new StubHttp((_, _) => Task.FromResult(StubHttp.Json(
            ++calls == 1 ? Geo(place) : GoodWeather))));
        var result = await new WeatherService(http).GetAsync("Берлин");
        Assert.Contains("Монтсеррат", result.PublicText);
        Assert.DoesNotContain("Parish", result.PublicText);
    }
}
