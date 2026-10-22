namespace IntellectBot.McpServer.Infrastructure;

// Короткий ограниченный кэш только публичных данных. Одновременные чтения одного ключа объединяются.
internal sealed class AsyncCache<TKey, TValue>(TimeProvider clock, int capacity = 256) where TKey : notnull
{
    private sealed record Entry(DateTimeOffset ExpiresAt, Lazy<Task<TValue>> Value);
    private readonly Dictionary<TKey, Entry> entries = [];
    private readonly object gate = new();

    public Task<TValue> GetAsync(TKey key, TimeSpan ttl, Func<CancellationToken, Task<TValue>> load, CancellationToken ct)
    {
        Entry entry;
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (entries.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
                entry = cached;
            else
            {
                foreach (var expired in entries.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                    entries.Remove(expired);
                MakeRoom(key);
                Entry? created = null;
                created = new(now + ttl, new Lazy<Task<TValue>>(() => LoadAsync(key, created!, load)));
                entries[key] = entry = created;
            }
        }
        // Отмена одного ожидающего не отменяет общий ограниченный HTTP-запрос для остальных.
        return entry.Value.Value.WaitAsync(ct);
    }

    public void Set(TKey key, TValue value, TimeSpan ttl)
    {
        lock (gate)
        {
            MakeRoom(key);
            entries[key] = new(clock.GetUtcNow() + ttl, new Lazy<Task<TValue>>(() => Task.FromResult(value)));
        }
    }

    public void Remove(TKey key)
    {
        lock (gate)
            entries.Remove(key);
    }

    private void MakeRoom(TKey key)
    {
        if (entries.Count >= capacity && !entries.ContainsKey(key))
            entries.Remove(entries.MinBy(pair => pair.Value.ExpiresAt).Key);
    }

    private async Task<TValue> LoadAsync(TKey key, Entry entry, Func<CancellationToken, Task<TValue>> load)
    {
        try
        {
            return await load(CancellationToken.None);
        }
        catch
        {
            lock (gate)
                if (entries.GetValueOrDefault(key) == entry)
                    entries.Remove(key);
            throw;
        }
    }
}
