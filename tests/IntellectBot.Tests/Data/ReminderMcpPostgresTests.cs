using System.Text.Json;
using IntellectBot.Data;
using IntellectBot.Mcp;
using IntellectBot.McpServer.Reminders;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace IntellectBot.Tests;

public sealed partial class PostgresTests
{
    [DatabaseFact]
    public async Task McpReminderRequiresConfirmationAndIsIdempotent()
    {
        var clock = new ReminderClock(
            new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));

        var store = new ReminderStore(
            source ?? throw new InvalidOperationException("Тестовая БД не инициализирована."),
            clock);

        await AgentStore.SetTimeZoneAsync(
            42, "Europe/Moscow", CancellationToken.None);

        var action = await store.PrepareAsync(
            42, 700, 0, "Проверить код",
            "2026-10-12T18:00:00+03:00");

        action = await store.ConfirmAsync(42, action.Id);

        var key = Enumerable.Range(1, 32)
            .Select(value => (byte)value).ToArray();

        var authorization = new ReminderAuthorization(key, clock);
        var endpoint = new ReminderEndpoints(
            authorization, new ReminderExecutor(store));

        var request = new CallToolRequestParams
        {
            Name = "add_reminder",
            Meta = authorization.CreateMetadata(42, action),
            Arguments = new Dictionary<string, JsonElement>
            {
                ["text"] = JsonSerializer.SerializeToElement("Проверить код"),
                ["remind_at"] = JsonSerializer.SerializeToElement(
                    "2026-10-12T18:00:00+03:00")
            }
        };

        var first = await endpoint.ExecuteRequestAsync(request);
        var repeated = await endpoint.ExecuteRequestAsync(request);

        Assert.Equal(first.Id, repeated.Id);
        Assert.True(first.Created);
        Assert.False(repeated.Created);
        Assert.Equal("Europe/Moscow", first.TimeZone);
        Assert.Equal(1L, await ReminderRowCountAsync());

        request.Arguments["text"] =
            JsonSerializer.SerializeToElement("Изменённый текст");

        await Assert.ThrowsAsync<McpException>(() =>
            endpoint.ExecuteRequestAsync(request));

        Assert.Equal(1L, await ReminderRowCountAsync());
    }
}
