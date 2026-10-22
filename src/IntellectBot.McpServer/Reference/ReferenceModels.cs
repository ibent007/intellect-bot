using System.Text.Json.Serialization;

namespace IntellectBot.McpServer.Reference;

public sealed record ReferenceCandidate(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("selection_arguments")] IReadOnlyDictionary<string, string> SelectionArguments);

public sealed record ReferenceResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("candidates")] ReferenceCandidate[] Candidates,
    [property: JsonPropertyName("public_text")] string PublicText,
    [property: JsonPropertyName("title")] string Title = "",
    [property: JsonPropertyName("url")] string Url = "",
    [property: JsonPropertyName("revision_id")] long RevisionId = 0,
    [property: JsonPropertyName("evidence_kind")] string EvidenceKind = "reference",
    [property: JsonPropertyName("facts")] string[]? Facts = null);
