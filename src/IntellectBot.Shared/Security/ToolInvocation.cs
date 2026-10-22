using System.Security.Cryptography;
using System.Text.Json;

namespace IntellectBot.Mcp;

// Подпись чтения связана с именем любого метода и всеми его аргументами.
public static class ToolInvocation
{
    public static string Operation(string name, JsonElement arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Write(writer, arguments);
        return "tool:" + name + ":" + Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
                Write(writer, item);
            writer.WriteEndArray();
        }
        else
            value.WriteTo(writer);
    }
}
