using System.Text.Json;

namespace IntellectBot.McpServer.Infrastructure;

// Ограниченная HTTP-граница фиксированных публичных источников.
internal sealed class SourceHttpClient(HttpClient http)
{
    private const int MaxResponseBytes = 1_048_576;

    public async Task<JsonDocument> GetJsonAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        deadline.CancelAfter(TimeSpan.FromSeconds(8));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            deadline.Token);

        if (!response.IsSuccessStatusCode)
            throw new SourceException(
                "unavailable",
                $"Сервис {new Uri(url).Host} вернул HTTP {(int)response.StatusCode}.");

        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new SourceException("invalid_response", "Источник вернул некорректные данные.");

        await using var stream =
            await response.Content.ReadAsStreamAsync(deadline.Token);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var count = await stream.ReadAsync(
                chunk.AsMemory(), deadline.Token);

            if (count == 0)
                break;

            if (buffer.Length + count > MaxResponseBytes)
                throw new SourceException("invalid_response", "Источник вернул некорректные данные.");

            buffer.Write(chunk, 0, count);
        }

        buffer.Position = 0;

        return await JsonDocument.ParseAsync(
            buffer, cancellationToken: deadline.Token);
    }

}

internal sealed class SourceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
