using System.Text.Json;
using IntellectBot.Data;
using IntellectBot.Mcp;
using IntellectBot.McpServer.Schedule;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Npgsql;
using Xunit;
namespace IntellectBot.Tests;

public sealed class PersonalScheduleAccessTests
{
    [Fact]
    public async Task UnsignedPersonalRequestIsRejectedBeforeDatabaseAccess()
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=unused;Database=unused;Timeout=1");
        var clock = new AgentClock();
        var endpoint = new ScheduleEndpoints(new ToolRequestAuthorization(new byte[32], clock),
            new PersonalScheduleReader(new PersonalScheduleStore(source), new AgentStateStore(source), clock));
        var request = new CallToolRequestParams { Name = "get_schedule", Arguments = new Dictionary<string, JsonElement> { ["date"] = JsonSerializer.SerializeToElement("2030-01-15") } };
        var error = await Assert.ThrowsAsync<McpException>(() => endpoint.ExecuteRequestAsync(request));
        Assert.Contains("доступа", error.Message);
    }
    [Fact]
    public async Task ModelCannotAddOwnerToScheduleArguments()
    {
        var clock = new AgentClock();
        var auth = new ToolRequestAuthorization(new byte[32], clock);
        var endpoint = new ScheduleEndpoints(auth);
        var request = new CallToolRequestParams
        {
            Name = "get_schedule",
            Meta = auth.Create(42, "day:2030-01-15"),
            Arguments = new Dictionary<string, JsonElement>
            {
                ["date"] = JsonSerializer.SerializeToElement("2030-01-15"),
                ["owner"] = JsonSerializer.SerializeToElement(99)
            }
        };
        var error = await Assert.ThrowsAsync<McpException>(() => endpoint.ExecuteRequestAsync(request));
        Assert.Contains("дата", error.Message);
    }
}
