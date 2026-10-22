using IntellectBot.Data;
using IntellectBot.Domain;
using Npgsql;
using Xunit;

namespace IntellectBot.Tests;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_DB_TESTS") != "1" &&
            Environment.GetEnvironmentVariable("RUN_LAB1_DB_TESTS") != "1")
            Skip = "Set RUN_DB_TESTS=1 and TEST_DATABASE_URL to run PostgreSQL tests.";
    }
}
public sealed class DatabaseTheoryAttribute : TheoryAttribute
{
    public DatabaseTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_DB_TESTS") != "1" &&
            Environment.GetEnvironmentVariable("RUN_LAB1_DB_TESTS") != "1")
            Skip = "Set RUN_DB_TESTS=1 and TEST_DATABASE_URL to run PostgreSQL tests.";
    }
}
[Trait("Category", "Integration")]
public sealed partial class PostgresTests : IAsyncLifetime
{
    private NpgsqlDataSource? source;
    private NpgsqlDataSource? admin;
    private string schema = "";
    private string scopedConnection = "";
    private DialogueStore Store => new(source!);
    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RUN_DB_TESTS") != "1" &&
            Environment.GetEnvironmentVariable("RUN_LAB1_DB_TESTS") != "1")
            return;
        var connection = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("TEST_DATABASE_URL is required; tests never fall back to production credentials.");
        admin = NpgsqlDataSource.Create(connection);
        schema = "intellect_test_" + Guid.NewGuid().ToString("N");
        // Имя создаётся приложением из фиксированного префикса и шестнадцатеричных цифр.
        await using (var command = admin.CreateCommand($"CREATE SCHEMA {schema}"))
            await command.ExecuteNonQueryAsync();
        scopedConnection = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ConnectionString;
        source = NpgsqlDataSource.Create(scopedConnection);
        await new DatabaseMigrator(source!).ApplyAsync(default);
    }
    public async Task DisposeAsync()
    {
        if (source is not null)
            await source.DisposeAsync();
        if (admin is not null)
        {
            await using var command = admin.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }
    [DatabaseFact]
    public async Task SchemaIsIdempotentAndDefaultsAreCorrect()
    {
        await new DatabaseMigrator(source!).ApplyAsync(default);
        Assert.True(await new DatabaseProbe(source!).PingAsync(default));
        Assert.Equal(new UserSettings("chat", .3), await Store.GetOrCreateAsync(42, default));
        Assert.Equal(new UserSettings("chat", .3), await Store.GetOrCreateAsync(42, default));
    }
    [DatabaseFact]
    public async Task HistorySurvivesNewPoolAndKeepsOrder()
    {
        await Store.GetOrCreateAsync(1, default);
        await Store.SaveTurnAsync(1, "a", "b", default);
        await Store.SaveTurnAsync(1, "c", "d", default);
        await using var restarted = NpgsqlDataSource.Create(scopedConnection);
        Assert.Equal(new[] { "a", "b", "c", "d" }, (await new DialogueStore(restarted).GetHistoryAsync(1, 12, default)).Select(m => m.Content));
        Assert.Equal(new[] { "c", "d" }, (await Store.GetHistoryAsync(1, 3, default)).Select(m => m.Content));
    }
    [DatabaseFact]
    public async Task ResetOnlyAffectsCallingUserAndPreservesSettings()
    {
        await Store.SetModeAsync(1, "review", default);
        await Store.SetTemperatureAsync(1, 1, default);
        await Store.SaveTurnAsync(1, "a", "b", default);
        await Store.GetOrCreateAsync(2, default);
        await Store.SaveTurnAsync(2, "other", "answer", default);
        await Store.ClearHistoryAsync(1, default);
        Assert.Empty(await Store.GetHistoryAsync(1, 12, default));
        Assert.Equal(2, (await Store.GetHistoryAsync(2, 12, default)).Count);
        Assert.Equal(new("review", 1), await Store.GetOrCreateAsync(1, default));
    }
    [DatabaseTheory]
    [InlineData("study")]
    [InlineData("review")]
    [InlineData("translate")]
    public async Task ModeIsPersistedAndClearsOldHistory(string mode)
    {
        await Store.SetTemperatureAsync(1, .7, default);
        await Store.SaveTurnAsync(1, "a", "b", default);
        await Store.SetModeAsync(1, mode, default);
        Assert.Empty(await Store.GetHistoryAsync(1, 12, default));
        Assert.Equal(new(mode, .7), await Store.GetOrCreateAsync(1, default));
    }
    [DatabaseTheory]
    [InlineData(0.0)]
    [InlineData(.3)]
    [InlineData(.7)]
    [InlineData(1.0)]
    public async Task TemperatureIsPersistedWithoutChangingHistory(double value)
    {
        await Store.SetModeAsync(1, "translate", default);
        await Store.SaveTurnAsync(1, "a", "b", default);
        await Store.SetTemperatureAsync(1, value, default);
        Assert.Equal(new("translate", value), await Store.GetOrCreateAsync(1, default));
        Assert.Equal(2, (await Store.GetHistoryAsync(1, 12, default)).Count);
    }
    [DatabaseFact]
    public async Task TurnIsAtomicWhenAnswerViolatesConstraint()
    {
        await Store.GetOrCreateAsync(1, default);
        await Assert.ThrowsAsync<PostgresException>(() => Store.SaveTurnAsync(1, "not saved", "", default));
        Assert.Empty(await Store.GetHistoryAsync(1, 12, default));
    }
    [DatabaseFact]
    public async Task ParameterizedSqlStoresQuotesAsData()
    {
        const string input = "'); DROP TABLE bot_users; --";
        await Store.GetOrCreateAsync(1, default);
        await Store.SaveTurnAsync(1, input, "answer", default);
        Assert.Equal(input, (await Store.GetHistoryAsync(1, 12, default))[0].Content);
        Assert.True(await new DatabaseProbe(source!).PingAsync(default));
    }
    [DatabaseFact]
    public async Task DeletingUserCascadesOnlyTheirMessages()
    {
        await Store.GetOrCreateAsync(1, default);
        await Store.SaveTurnAsync(1, "a", "b", default);
        await using var command = source!.CreateCommand("DELETE FROM bot_users WHERE chat_id=$1");
        command.Parameters.AddWithValue(1L);
        await command.ExecuteNonQueryAsync();
        Assert.Empty(await Store.GetHistoryAsync(1, 12, default));
    }
    [DatabaseTheory]
    [InlineData("INSERT INTO bot_users(chat_id,mode) VALUES (1,'invalid')")]
    [InlineData("INSERT INTO bot_users(chat_id,temperature) VALUES (1,0.5)")]
    [InlineData("INSERT INTO dialogue_messages(chat_id,role,content) VALUES (999,'user','a')")]
    public async Task ConstraintsEnforceSchema(string sql)
    {
        await using var command = source!.CreateCommand(sql);
        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }
}
