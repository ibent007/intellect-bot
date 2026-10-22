using System.Text.Json.Serialization;

namespace IntellectBot.Domain;

public sealed record ChatMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content)
{
    public override string ToString() => $"ChatMessage({Role}, [hidden])";
}
