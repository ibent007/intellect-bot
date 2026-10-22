using System.Text.Json;
using System.Text.Json.Nodes;
using IntellectBot.Domain;
using IntellectBot.Mcp;
using IntellectBot.McpServer.Reminders;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace IntellectBot.Tests;

public sealed class ReminderAuthorizationTests
{
    private const string Text = "Проверить код";
    private const string When = "2026-10-20T18:00:00+03:00";

    private static readonly byte[] Key =
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void SignedContextPreservesOwnerActionAndParameters()
    {
        var clock = new AuthorizationClock();
        var authorization = new ReminderAuthorization(Key, clock);
        var action = ConfirmedAction(clock);

        var metadata = authorization.CreateMetadata(42, action);
        var result = authorization.Verify(metadata, Text, When);

        Assert.Equal(42L, result.OwnerChatId);
        Assert.Equal(action.Id, result.ActionId);
        Assert.Equal(action.Draft, result.Draft);
    }

    [Fact]
    public void MissingContextIsRejected()
    {
        var authorization = new ReminderAuthorization(
            Key, new AuthorizationClock());

        var error = Assert.Throws<ReminderException>(() =>
            authorization.Verify(null, Text, When));

        Assert.Equal("unauthorized", error.Code);
    }

    [Fact]
    public void DisabledAuthorizationRejectsWrites()
    {
        var clock = new AuthorizationClock();
        var metadata = new ReminderAuthorization(Key, clock)
            .CreateMetadata(42, ConfirmedAction(clock));

        var disabled = new ReminderAuthorization(null, clock);

        Assert.Throws<ReminderException>(() =>
            disabled.Verify(metadata, Text, When));
    }

    [Fact]
    public void AnotherSessionKeyIsRejected()
    {
        var clock = new AuthorizationClock();
        var metadata = new ReminderAuthorization(Key, clock)
            .CreateMetadata(42, ConfirmedAction(clock));

        var otherKey = Enumerable.Repeat((byte)99, 32).ToArray();
        var anotherSession = new ReminderAuthorization(otherKey, clock);

        Assert.Throws<ReminderException>(() =>
            anotherSession.Verify(metadata, Text, When));
    }

    [Theory]
    [InlineData("Другой текст", When)]
    [InlineData(Text, "2026-10-20T19:00:00+03:00")]
    public void ChangedParametersAreRejected(string text, string when)
    {
        var clock = new AuthorizationClock();
        var authorization = new ReminderAuthorization(Key, clock);
        var metadata = authorization.CreateMetadata(
            42, ConfirmedAction(clock));

        Assert.Throws<ReminderException>(() =>
            authorization.Verify(metadata, text, when));
    }

    [Fact]
    public void ChangedOwnerWithoutNewSignatureIsRejected()
    {
        var clock = new AuthorizationClock();
        var authorization = new ReminderAuthorization(Key, clock);
        var metadata = authorization.CreateMetadata(
            42, ConfirmedAction(clock));

        var token = metadata[ReminderAuthorization.MetadataKey]!
            .GetValue<string>();

        var parts = token.Split('.');
        var payload = JsonNode.Parse(
            Convert.FromBase64String(parts[0]))!.AsObject();

        payload["OwnerChatId"] = 99;

        metadata[ReminderAuthorization.MetadataKey] =
            $"{Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload))}" +
            $".{parts[1]}";

        Assert.Throws<ReminderException>(() =>
            authorization.Verify(metadata, Text, When));
    }

    [Fact]
    public void ExpiredContextIsRejected()
    {
        var clock = new AuthorizationClock();
        var authorization = new ReminderAuthorization(Key, clock);
        var metadata = authorization.CreateMetadata(
            42, ConfirmedAction(clock));

        clock.Current = clock.Current.AddMinutes(5);

        Assert.Throws<ReminderException>(() =>
            authorization.Verify(metadata, Text, When));
    }

    [Fact]
    public void ClockBeforeIssueTimeIsRejected()
    {
        var clock = new AuthorizationClock();
        var authorization = new ReminderAuthorization(Key, clock);
        var metadata = authorization.CreateMetadata(
            42, ConfirmedAction(clock));

        clock.Current = clock.Current.AddSeconds(-1);

        Assert.Throws<ReminderException>(() =>
            authorization.Verify(metadata, Text, When));
    }

    [Fact]
    public void PendingActionCannotReceiveSignedContext()
    {
        var clock = new AuthorizationClock();
        var authorization = new ReminderAuthorization(Key, clock);
        var pending = ConfirmedAction(clock) with
        {
            Status = "pending",
            ConfirmedAt = null
        };

        Assert.Throws<ReminderException>(() =>
            authorization.CreateMetadata(42, pending));
    }

    [Fact]
    public async Task MissingContextDoesNotReachExecutor()
    {
        var executor = new RecordingExecutor();
        var endpoint = new ReminderEndpoints(
            new ReminderAuthorization(Key, new AuthorizationClock()),
            executor);

        await Assert.ThrowsAsync<McpException>(() =>
            endpoint.ExecuteRequestAsync(Request(null)));

        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task ExtraOwnerArgumentIsRejected()
    {
        var clock = new AuthorizationClock();
        var authorization = new ReminderAuthorization(Key, clock);
        var executor = new RecordingExecutor();
        var endpoint = new ReminderEndpoints(authorization, executor);

        var request = Request(authorization.CreateMetadata(
            42, ConfirmedAction(clock)));

        request.Arguments!["owner_chat_id"] =
            JsonSerializer.SerializeToElement(99);

        await Assert.ThrowsAsync<McpException>(() =>
            endpoint.ExecuteRequestAsync(request));

        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task AuthorizedRequestReturnsLocalTimeAndSchemaFields()
    {
        var clock = new AuthorizationClock();
        var authorization = new ReminderAuthorization(Key, clock);
        var executor = new RecordingExecutor();
        var endpoint = new ReminderEndpoints(authorization, executor);

        var result = await endpoint.ExecuteRequestAsync(
            Request(authorization.CreateMetadata(
                42, ConfirmedAction(clock))));

        Assert.Equal(1, executor.Calls);
        Assert.Equal("Europe/Moscow", result.TimeZone);
        Assert.Equal("2026-10-20T18:00:00.000000+03:00", result.RemindAt);
        Assert.True(result.Created);

        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.TryGetProperty("id", out _));
        Assert.True(json.TryGetProperty("remind_at", out _));
        Assert.True(json.TryGetProperty("timezone", out _));
        Assert.True(json.TryGetProperty("created", out _));
    }

    private static PreparedReminder ConfirmedAction(AuthorizationClock clock) =>
        new(
            Guid.NewGuid(),
            ReminderValidation.Parse(Text, When, "Europe/Moscow"),
            "confirmed",
            clock.Current,
            clock.Current.AddMinutes(5),
            clock.Current);

    private static CallToolRequestParams Request(JsonObject? metadata) =>
        new()
        {
            Name = "add_reminder",
            Meta = metadata,
            Arguments = new Dictionary<string, JsonElement>
            {
                ["text"] = JsonSerializer.SerializeToElement(Text),
                ["remind_at"] = JsonSerializer.SerializeToElement(When)
            }
        };

    private sealed class AuthorizationClock : TimeProvider
    {
        public DateTimeOffset Current
        {
            get; set;
        } =
            new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class RecordingExecutor : IReminderExecutor
    {
        public int Calls
        {
            get; private set;
        }

        public Task<ReminderResult> ExecuteAsync(
            AuthorizedReminder reminder,
            CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(new ReminderResult(
                Guid.NewGuid(),
                reminder.Draft.RemindAt,
                reminder.Draft.TimeZone,
                true));
        }
    }
}
