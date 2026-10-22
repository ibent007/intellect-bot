using System.Text.Encodings.Web;
using System.Text.Json;

namespace IntellectBot.Infrastructure;

// JSON для HTTP и MCP-данных, а не для вставки в HTML.
// Русский текст остаётся читаемым и не раздувает контекст последовательностями \uXXXX.
public static class JsonDefaults
{
    public static JsonSerializerOptions Compact
    {
        get;
    } = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 64
    };

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Compact);
    public static string Text<T>(T value) => JsonSerializer.Serialize(value, Compact);
    public static JsonElement ParseObject(string value)
    {
        try
        {
            using var parsed = JsonDocument.Parse(value);
            return parsed.RootElement.Clone();
        }
        catch (JsonException) { return default; }
    }
}
