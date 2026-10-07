namespace IntellectBot.Hosting;

public sealed record HealthResult(int StatusCode, string Status, string Database, string Polling);
public static class HealthProbe
{
    public static async Task<HealthResult> CheckAsync(RuntimeState state, Func<CancellationToken, Task<bool>> ping, CancellationToken ct)
    {
        var database = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            database = await ping(timeout.Token);
        }
        catch (Exception) { /* Only public readiness flags, never exception text or credentials. */ }
        var ready = state.Initialized && state.Polling && database;
        return new(ready ? 200 : 503, ready ? "ok" : "not_ready", database ? "ok" : "unavailable", state.Polling ? "running" : "stopped");
    }
}
