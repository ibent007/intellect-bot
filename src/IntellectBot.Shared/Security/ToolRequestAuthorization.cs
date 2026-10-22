using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntellectBot.Domain;
namespace IntellectBot.Mcp;

// Подписывает владельца и точную операцию MCP; модель не получает служебный контекст.
public sealed class ToolRequestAuthorization(byte[]? key, TimeProvider clock)
{
    public const string MetadataKey = "intellect/actor";
    public JsonObject Create(long owner, string operation)
    {
        AgentStateValidation.ValidateOwner(owner);
        if (key is not { Length: 32 })
            throw new UnauthorizedAccessException("Нет доверенного контекста приложения.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Context(owner, operation, clock.GetUtcNow().ToUnixTimeSeconds()));
        return new JsonObject { [MetadataKey] = Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(key, bytes)) };
    }
    public long Verify(JsonObject? metadata, string operation)
    {
        try
        {
            if (key is not { Length: 32 } || metadata?[MetadataKey] is not JsonValue value ||
                !value.TryGetValue<string>(out var token) || token is null || token.Length > 2048)
                throw new FormatException();
            var parts = token.Split('.');
            if (parts.Length != 2)
                throw new FormatException();
            var bytes = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(key, bytes)))
                throw new FormatException();
            var context = JsonSerializer.Deserialize<Context>(bytes) ?? throw new FormatException();
            var age = clock.GetUtcNow().ToUnixTimeSeconds() - context.Issued;
            if (context.Owner <= 0 || context.Operation != operation || age < 0 || age > 60)
                throw new FormatException();
            return context.Owner;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
        {
            throw new UnauthorizedAccessException("Нет действующего доступа к личным данным.");
        }
    }
    private sealed record Context(long Owner, string Operation, long Issued);
}
