using IntellectBot.Infrastructure;
using IntellectBot.Telegram;

namespace IntellectBot.Hosting;

public sealed class RuntimeState
{
    public volatile bool Initialized;
    public volatile bool Polling;
}
public sealed class PollingService(TelegramApi api, UpdateHandler handler, RuntimeState state, SafeLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var offset = 0L;
        var delaySeconds = 1;
        var pending = new List<Task>();
        state.Polling = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var updates = await api.GetUpdatesAsync(offset, stoppingToken);
                    delaySeconds = 1;
                    foreach (var update in updates)
                    {
                        // Invoke in Telegram order; async per-chat gates are acquired before the next update.
                        pending.Add(handler.HandleAsync(update, stoppingToken));
                        offset = update.UpdateId + 1;
                    }
                    pending.RemoveAll(t => t.IsCompletedSuccessfully);
                    // Bound outstanding work without losing or reordering updates.
                    if (pending.Count >= 100)
                    {
                        await Task.WhenAll(pending);
                        pending.Clear();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) when (error is TelegramException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
                {
                    log.Failure("Сбой Telegram polling", error);
                    var wait = error is TelegramException { RetryAfter: > 0 } telegram ? Math.Clamp(telegram.RetryAfter.Value, 1, 60) : delaySeconds;
                    await Task.Delay(TimeSpan.FromSeconds(wait), stoppingToken);
                    delaySeconds = Math.Min(delaySeconds * 2, 30);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            state.Polling = false;
            await Task.WhenAll(pending);
            log.Write("Polling остановлен.");
        }
    }
}
