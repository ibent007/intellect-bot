using System.Text.Json;
using IntellectBot.Agent;
using IntellectBot.Data;
using IntellectBot.Domain;
using IntellectBot.Mcp;
using IntellectBot.McpServer.Reminders;
using ModelContextProtocol.Protocol;
using Xunit;

namespace IntellectBot.Tests;

public sealed partial class PostgresTests
{
    [DatabaseFact]
    public async Task UiConcurrentConfirmationsThroughMcpCreateOneReminder()
    {
        var (service, store, clock) = UiServices();
        await AgentStore.SetTimeZoneAsync(42, "Europe/Moscow", CancellationToken.None);
        var action = await store.PrepareAsync(42, 800, 0, "Задание", "2026-10-12T18:00:00+03:00");
        await Task.WhenAll(service.ReminderAsync(42, action.Id, true, CancellationToken.None),
            service.ReminderAsync(42, action.Id, true, CancellationToken.None));
        Assert.Equal(1L, await ReminderRowCountAsync());
        Assert.Equal("executed", (await store.GetAsync(42, action.Id))!.Status);
        clock.Now = clock.Now.AddDays(1);
        var repeated = await service.ReminderAsync(42, action.Id, true, CancellationToken.None);
        Assert.Contains("уже сохранено", repeated.Text);
        Assert.Equal(1L, await ReminderRowCountAsync());
    }

    [DatabaseFact]
    public async Task UiCancellationAndExpiryDoNotCreateReminders()
    {
        var (service, store, clock) = UiServices();
        await AgentStore.SetTimeZoneAsync(42, "Europe/Moscow", CancellationToken.None);
        var cancelled = await store.PrepareAsync(42, 801, 0, "Первое", "2026-10-12T18:00:00+03:00");
        await service.ReminderAsync(42, cancelled.Id, false, CancellationToken.None);
        Assert.Equal("cancelled", (await store.GetAsync(42, cancelled.Id))!.Status);
        var expired = await store.PrepareAsync(42, 802, 0, "Второе", "2026-10-12T18:00:00+03:00");
        clock.Now = clock.Now.AddMinutes(5);
        await service.ReminderAsync(42, expired.Id, true, CancellationToken.None);
        Assert.Equal("expired", (await store.GetAsync(42, expired.Id))!.Status);
        Assert.Equal(0L, await ReminderRowCountAsync());
    }

    [DatabaseFact]
    public async Task UiZoneAndWhyAreOwnedAndPersistent()
    {
        var (service, store, _) = UiServices();
        await service.TimeZoneAsync(42, "Europe/Moscow", CancellationToken.None);
        var action = await store.PrepareAsync(42, 803, 0, "Секретный текст", "2026-10-12T18:00:00+03:00");
        await service.ReminderAsync(42, action.Id, true, CancellationToken.None);
        Assert.Contains("Напоминание", await service.WhyAsync(42, CancellationToken.None));
        Assert.DoesNotContain("Секретный текст", await service.WhyAsync(42, CancellationToken.None));
        Assert.Contains("Пока нет", await service.WhyAsync(99, CancellationToken.None));
        Assert.Contains("другому пользователю", (await service.ReminderAsync(99, action.Id, true, CancellationToken.None)).Text);
        await service.TimeZoneAsync(42, "Asia/Tokyo", CancellationToken.None);
        Assert.Equal("Europe/Moscow", (await store.GetAsync(42, action.Id))!.Draft.TimeZone);
        Assert.Equal("Asia/Tokyo", await new AgentStateStore(source!).GetTimeZoneAsync(42, CancellationToken.None));
    }

    private (AgentUiService Service, ReminderStore Store, UiClock Clock) UiServices()
    {
        var clock = new UiClock();
        var store = new ReminderStore(source ?? throw new InvalidOperationException("Тестовая БД не готова."), clock);
        var authorization = new ReminderAuthorization(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(), clock);
        var endpoint = new ReminderEndpoints(authorization, new ReminderExecutor(store));
        var gateway = new UiDatabaseGateway(endpoint, authorization);
        return (new AgentUiService(AgentStore, store, () => gateway, clock), store, clock);
    }

    private sealed class UiDatabaseGateway(ReminderEndpoints endpoint, ReminderAuthorization authorization) : IConfirmedReminderGateway
    {
        public McpToolCatalog Catalog { get; init; } = new(AgentTestSupport.Tools);
        public Task<McpToolOutcome> CallReadOnlyAsync(string name, JsonElement args, CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpToolOutcome(false, null, "not_used"));
        public async Task<McpToolOutcome> ExecuteConfirmedReminderAsync(long owner, PreparedReminder action, CancellationToken cancellationToken = default)
        {
            var result = await endpoint.ExecuteRequestAsync(new CallToolRequestParams
            {
                Name = "add_reminder",
                Meta = authorization.CreateMetadata(owner, action),
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["text"] = JsonSerializer.SerializeToElement(action.Draft.Text),
                    ["remind_at"] = JsonSerializer.SerializeToElement(ReminderAuthorization.FormatTime(action.Draft.RemindAt))
                }
            }, cancellationToken);
            return new(true, JsonSerializer.SerializeToElement(result), null);
        }
    }
}
