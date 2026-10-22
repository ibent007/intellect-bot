using System.Text.Json;
using IntellectBot.Agent;
using IntellectBot.Domain;
using IntellectBot.Llm;
using IntellectBot.Telegram;
using Xunit;

namespace IntellectBot.Tests;

public sealed class CurrentRegressionTests
{
    [Fact]
    public void LongAnswerDoesNotErasePreviousQuestion()
    {
        var history = new ChatMessage[] { new("user", "Объясни SOLID и GRASP"), new("assistant", new string('а', 8000)) };
        var messages = ContextBuilder.Build("chat", history, "Сравни их", 12, 6000);
        Assert.Contains(messages, message => message.Content == "Объясни SOLID и GRASP");
        Assert.Contains(messages, message => message.Content.Contains("сокращён только для контекста", StringComparison.Ordinal));
    }

    [Fact]
    public void TranslationFenceIsNotShownInsideCopyableText()
    {
        var parts = MessageFormatter.Format(new("Русский → Немецкий\n\n```text\nGeht schlafen, liebe Menschen.\n```")).ToArray();
        var text = string.Concat(parts.Select(part => part.Text));
        Assert.Contains("<code>Geht schlafen, liebe Menschen.</code>", text);
        Assert.DoesNotContain("```", text);
        Assert.False(TranslationReply.IsValid("Язык оригинала → Язык перевода\n\nИсходник → перевод"));
    }

    [Fact]
    public async Task TelegramDisablesLinkPreviewsOnSendAndEdit()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttp(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.True(body.RootElement.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
            calls++;
            return StubHttp.Json("{\"ok\":true,\"result\":true}");
        }));
        var api = new TelegramApi(http, "fake");
        await api.SendAsync(1, "https://example.com", null, null, default);
        await api.EditAsync(1, 2, "https://example.com", new InlineKeyboard([]), default);
        Assert.Equal(2, calls);
    }

    private static JsonElement[] Evidence() =>
    [
        AgentTestSupport.Json(new { role = "user", content = "Кто это?" }),
        AgentTestSupport.Json(new { role = "tool", tool_call_id = "ref", content = JsonSerializer.Serialize(new
        {
            success = true,
            data = new { status = "ok", title = "Имя", url = "https://ru.wikipedia.org/w/index.php?oldid=1",
                public_text = "Проверенный резервный ответ", facts = new[] { "Факт первый.", "Факт второй." } }
        }) })
    ];

    [Fact]
    public void EvidenceUsesOnlySourceTextAndFixedHeadings()
    {
        var text = EvidencePresentation.Render(Evidence(), "{\"sections\":[{\"heading\":\"Кратко\",\"facts\":[1,0]}]}");
        Assert.Contains("Факт второй.\n\n• Факт первый.", text);
        Assert.Contains("CC BY-SA", text);
        Assert.Equal("Проверенный резервный ответ", EvidencePresentation.Render(Evidence(),
            "{\"sections\":[{\"heading\":\"Кратко\",\"facts\":[99]}]}"));
        Assert.Equal("Проверенный резервный ответ", EvidencePresentation.Render(Evidence(),
            "{\"sections\":[{\"heading\":\"Выдуманный факт\",\"facts\":[0]}]}"));
    }

    [Fact]
    public void ToolWindowPreservesPairAndDropsOldContextAndDuplicatedExcerpt()
    {
        var messages = new List<JsonElement>
        {
            AgentTestSupport.Json(new { role = "system", content = new string('с', 5000) }),
            AgentTestSupport.Json(new { role = "user", content = "Старая тема" }),
            AgentTestSupport.Json(new { role = "assistant", content = new string('а', 8000) }),
            Evidence()[0],
            AgentToolProtocol.CallMessage([AgentTestSupport.Call("ref", "get_reference", new { topic = "Имя" })]),
            Evidence()[1]
        };
        var wire = ToolReplyWindow.Build(messages);
        var json = JsonSerializer.Serialize(wire);
        Assert.DoesNotContain("Старая тема", json);
        Assert.Equal("ref", wire[^1].GetProperty("tool_call_id").GetString());
        Assert.Equal("ref", wire[^2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.True(json.Length < JsonSerializer.Serialize(messages).Length / 2);
    }
}
