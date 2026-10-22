namespace IntellectBot.Infrastructure;

// Счётчик ссылок освобождает блокировки чатов после обработки.
public sealed class ChatLocks
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1); public int References;
    }
    private readonly Dictionary<long, Entry> entries = [];
    private readonly object sync = new();
    public async Task<IDisposable> AcquireAsync(long chatId, CancellationToken ct)
    {
        Entry entry;
        lock (sync)
        {
            if (!entries.TryGetValue(chatId, out entry!))
                entries[chatId] = entry = new();
            entry.References++;
        }
        try
        {
            await entry.Gate.WaitAsync(ct);
        }
        catch { ReleaseReference(chatId, entry); throw; }
        return new Lease(() => { entry.Gate.Release(); ReleaseReference(chatId, entry); });
    }
    private void ReleaseReference(long id, Entry entry)
    {
        lock (sync)
            if (--entry.References == 0)
            {
                entries.Remove(id);
                entry.Gate.Dispose();
            }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}
