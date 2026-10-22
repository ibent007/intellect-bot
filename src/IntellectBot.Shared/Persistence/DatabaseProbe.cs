using Npgsql;
namespace IntellectBot.Data;

// Быстрая проверка соединения, не изменяющая данные
public sealed class DatabaseProbe(NpgsqlDataSource source)
{
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT 1");
        command.CommandTimeout = 3;
        return (int)(await command.ExecuteScalarAsync(ct))! == 1;
    }
}
