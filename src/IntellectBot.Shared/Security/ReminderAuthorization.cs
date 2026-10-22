using System.Text.Json.Serialization;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntellectBot.Domain;

namespace IntellectBot.Mcp;

public sealed record AuthorizedReminder(
    long OwnerChatId,
    Guid ActionId,
    ReminderDraft Draft)
{
    public override string ToString() => nameof(AuthorizedReminder);
}

public sealed class ReminderAuthorization
{
    public const string MetadataKey = "intellect/reminder";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    private readonly byte[]? key;
    private readonly TimeProvider clock;

    public ReminderAuthorization(byte[]? key, TimeProvider clock)
    {
        if (key is not null && key.Length != 32)
            throw new ArgumentException("Ключ должен содержать 32 байта.", nameof(key));

        this.key = key?.ToArray();
        this.clock = clock;
    }

    // Вызывается приложением после подтверждения пользователя.
    // Возвращаемые данные не передаются модели.
    public JsonObject CreateMetadata(long ownerChatId, PreparedReminder action)
    {
        AgentStateValidation.ValidateOwner(ownerChatId);

        var now = ReminderValidation.ToDatabaseTime(clock.GetUtcNow());

        if (key is null ||
            action.Id == Guid.Empty ||
            action.Status is not ("confirmed" or "executed") ||
            action.ConfirmedAt is null ||
            action.ConfirmedAt > now ||
            action.ExpiresAt <= now)
        {
            throw Denied();
        }

        var draft = ReminderValidation.Parse(
            action.Draft.Text,
            FormatTime(action.Draft.RemindAt),
            action.Draft.TimeZone);

        if (draft != action.Draft)
            throw Denied();

        var expiresAt = action.ExpiresAt < now.AddMinutes(5)
            ? action.ExpiresAt
            : now.AddMinutes(5);

        var payload = new SignedContext(
            ownerChatId,
            action.Id,
            HashText(draft.Text),
            draft.RemindAt.UtcDateTime.Ticks,
            draft.TimeZone,
            now.UtcDateTime.Ticks,
            expiresAt.UtcDateTime.Ticks);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var signature = HMACSHA256.HashData(key, bytes);

        return new JsonObject
        {
            [MetadataKey] =
                $"{Convert.ToBase64String(bytes)}.{Convert.ToBase64String(signature)}"
        };
    }

    public AuthorizedReminder Verify(
        JsonObject? metadata,
        string text,
        string remindAt)
    {
        if (key is null ||
            metadata?[MetadataKey] is not JsonValue value ||
            !value.TryGetValue<string>(out var token) ||
            string.IsNullOrEmpty(token) ||
            token.Length > 4096)
        {
            throw Denied();
        }

        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2)
                throw Denied();

            var bytes = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);

            if (bytes.Length > 2048 ||
                signature.Length != 32 ||
                !CryptographicOperations.FixedTimeEquals(
                    signature,
                    HMACSHA256.HashData(key, bytes)))
            {
                throw Denied();
            }

            var payload = JsonSerializer.Deserialize<SignedContext>(
                bytes, JsonOptions) ?? throw Denied();

            var nowTicks = clock.GetUtcNow().UtcDateTime.Ticks;

            if (payload.OwnerChatId <= 0 ||
                payload.ActionId == Guid.Empty ||
                payload.IssuedAtUtcTicks <= 0 ||
                payload.IssuedAtUtcTicks > nowTicks ||
                payload.ExpiresAtUtcTicks <= nowTicks ||
                payload.ExpiresAtUtcTicks > DateTime.MaxValue.Ticks ||
                payload.ExpiresAtUtcTicks <= payload.IssuedAtUtcTicks ||
                payload.ExpiresAtUtcTicks - payload.IssuedAtUtcTicks >
                    TimeSpan.FromMinutes(5).Ticks)
            {
                throw Denied();
            }

            var draft = ReminderValidation.Parse(
                text, remindAt, payload.TimeZone);

            if (HashText(draft.Text) != payload.TextHash ||
                draft.RemindAt.UtcDateTime.Ticks != payload.RemindAtUtcTicks)
            {
                throw Denied();
            }

            return new AuthorizedReminder(
                payload.OwnerChatId,
                payload.ActionId,
                draft);
        }
        catch (Exception exception) when (
            exception is FormatException or
                JsonException or
                ArgumentException or
                ReminderException)
        {
            throw Denied();
        }
    }

    public static string FormatTime(DateTimeOffset value) =>
        ReminderValidation.ToDatabaseTime(value).ToString(
            "yyyy-MM-dd'T'HH:mm:ss.ffffffzzz",
            CultureInfo.InvariantCulture);

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static ReminderException Denied() =>
        new("unauthorized", "Нет действующего подтверждения для этого действия.");

    private sealed record SignedContext(
        long OwnerChatId,
        Guid ActionId,
        string TextHash,
        long RemindAtUtcTicks,
        string TimeZone,
        long IssuedAtUtcTicks,
        long ExpiresAtUtcTicks);
}
