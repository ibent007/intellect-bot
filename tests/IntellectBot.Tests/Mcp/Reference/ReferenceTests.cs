using System.Net;
using System.Text.Json;
using IntellectBot.McpServer.Reference;
using Xunit;

namespace IntellectBot.Tests;

public sealed class ReferenceTests
{
    private static object Page(int id = 10, string title = "Эйнауди, Людовико", string extract = "Итальянский композитор и пианист.", string? entity = null) => new
    {
        pageid = id,
        title,
        extract,
        lastrevid = 12345L,
        pageprops = entity is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["wikibase_item"] = entity }
    };
    private static string Pages(params object[] pages) => JsonSerializer.Serialize(new { query = new { pages } });

    [Fact]
    public async Task ExcerptAndPermanentLinkComeFromSourceAndAreCached()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttp((request, _) =>
        {
            calls++;
            Assert.Equal("ru.wikipedia.org", request.RequestUri!.Host);
            Assert.Contains("explaintext=1", request.RequestUri.Query);
            return Task.FromResult(StubHttp.Json(Pages(Page())));
        }));
        var service = new ReferenceService(http);
        var first = await service.GetAsync("Людовико Эйнауди");
        var again = await service.GetAsync("Людовико Эйнауди");
        Assert.Equal("ok", first.Status);
        Assert.Contains("Итальянский композитор и пианист.", first.PublicText);
        Assert.Equal("https://ru.wikipedia.org/w/index.php?oldid=12345", first.Url);
        Assert.Contains(first.Url, first.PublicText);
        Assert.Contains("CC BY-SA", first.PublicText);
        Assert.Same(first, again);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AmbiguousNameReturnsOnlyRealPageIdsAndSelectionArguments()
    {
        using var http = new HttpClient(new StubHttp((_, _) => Task.FromResult(StubHttp.Json(Pages(
            Page(10, "Дуров, Николай"), Page(20, "Дуров, Павел"))))));
        var result = await new ReferenceService(http).GetAsync("Дуров");
        Assert.Equal("ambiguous", result.Status);
        Assert.Equal(new[] { 10, 20 }, result.Candidates.Select(item => item.Id));
        Assert.Equal("20", result.Candidates[1].SelectionArguments["pageId"]);
        Assert.Equal("Дуров, Павел", result.Candidates[1].SelectionArguments["topic"]);
        Assert.Empty(result.PublicText);
    }

    [Fact]
    public async Task ExactSelectedArticleUsesPageIdsInsteadOfAnotherSearch()
    {
        using var http = new HttpClient(new StubHttp((request, _) =>
        {
            Assert.Contains("pageids=20", request.RequestUri!.Query);
            Assert.DoesNotContain("generator=search", request.RequestUri.Query);
            return Task.FromResult(StubHttp.Json(Pages(Page(20, "Дуров, Павел"))));
        }));
        Assert.Equal("ok", (await new ReferenceService(http).GetAsync("Дуров, Павел", "20")).Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"query\":{\"pages\":[]}}")]
    public async Task MissingTopicDoesNotMakeUpAnAnswer(string json)
    {
        using var http = new HttpClient(new StubHttp((_, _) => Task.FromResult(StubHttp.Json(json))));
        var result = await new ReferenceService(http).GetAsync("Неизвестное имя");
        Assert.Equal("not_found", result.Status);
        Assert.Empty(result.Candidates);
        Assert.Contains("Уточните", result.PublicText);
    }

    [Fact]
    public async Task SourceFailureIsSafeAndDoesNotRemainCached()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttp((_, _) => Task.FromResult(++calls == 1
            ? StubHttp.Json("secret provider body", HttpStatusCode.ServiceUnavailable)
            : StubHttp.Json(Pages(Page())))));
        var service = new ReferenceService(http);
        var failed = await service.GetAsync("Людовико Эйнауди");
        Assert.Equal("unavailable", failed.Status);
        Assert.DoesNotContain("secret", failed.PublicText);
        Assert.Equal("ok", (await service.GetAsync("Людовико Эйнауди")).Status);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("2026-09-11", "28")]
    [InlineData("2026-09-12", "29")]
    public async Task AgeUsesSourceDateAndSuppliedLocalDay(string date, string age)
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttp((request, _) =>
        {
            calls++;
            if (calls == 1)
                return Task.FromResult(StubHttp.Json(Pages(Page(10, "Суини, Сидни", entity: "Q999"))));
            Assert.Equal("www.wikidata.org", request.RequestUri!.Host);
            return Task.FromResult(StubHttp.Json("""
                {"entities":{"Q999":{"claims":{"P569":[{"rank":"normal","mainsnak":{"datavalue":{"value":
                {"time":"+1997-09-12T00:00:00Z","precision":11,"calendarmodel":"http://www.wikidata.org/entity/Q1985727"}}}}]}}}}
                """));
        }));
        var result = await new ReferenceService(http).GetAsync("Сидни Суини", onDate: DateOnly.Parse(date));
        Assert.Contains("Дата рождения: 12.09.1997", result.PublicText);
        Assert.Contains(": " + age + ".", result.PublicText);
    }
    [Fact]
    public async Task PersonIsNotConfusedWithFilmAndTitleUsesNaturalOrder()
    {
        using var http = new HttpClient(new StubHttp((_, _) => Task.FromResult(StubHttp.Json(Pages(
            Page(10, "Джобс, Стив"), Page(20, "Стив Джобс (фильм)"))))));
        var result = await new ReferenceService(http).GetAsync("Стив Джобс", entityType: "person");
        Assert.Equal("ok", result.Status);
        Assert.Equal("Стив Джобс", result.Title);
        Assert.StartsWith("Стив Джобс\n", result.PublicText);
    }

    [Fact]
    public async Task TelegramIntentReturnsVerifiedUsernameInsteadOfBiography()
    {
        using var http = new HttpClient(new StubHttp((request, _) => Task.FromResult(StubHttp.Json(
            request.RequestUri!.Host == "ru.wikipedia.org" ? Pages(Page(10, "Дуров, Павел", entity: "Q1")) :
            """{"entities":{"Q1":{"claims":{"P3789":[{"mainsnak":{"datavalue":{"value":"durov"}}}]}}}}"""))));
        var result = await new ReferenceService(http).GetAsync("Павел Дуров", intent: "telegram");
        Assert.Contains("https://t.me/durov", result.PublicText);
        Assert.DoesNotContain("композитор", result.PublicText);
        Assert.Null(result.Facts);
    }

    [Fact]
    public async Task TelegramIntentDoesNotGuessMissingUrl()
    {
        using var http = new HttpClient(new StubHttp((_, _) => Task.FromResult(StubHttp.Json(Pages(Page())))));
        var result = await new ReferenceService(http).GetAsync("Людовико Эйнауди", intent: "telegram");
        Assert.Contains("Не буду угадывать", result.PublicText);
        Assert.DoesNotContain("t.me/", result.PublicText);
    }

    [Fact]
    public async Task CharacterSearchDoesNotOfferOtherFilmsWithDifferentTitles()
    {
        using var http = new HttpClient(new StubHttp((_, _) => Task.FromResult(StubHttp.Json(Pages(
            Page(10, "Джон Крамер (персонаж)"), Page(20, "Пила X"), Page(30, "Пила: Спираль"))))));
        var result = await new ReferenceService(http).GetAsync("Джон Крамер", entityType: "person");
        Assert.Equal("ok", result.Status);
        Assert.Empty(result.Candidates);
    }
}
