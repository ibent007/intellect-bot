using System.Text.Json;
using IntellectBot.Mcp;
using Xunit;

namespace IntellectBot.Tests;

public sealed class ToolInvocationTests
{
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    [Fact]
    public void SignatureOperationIsStableWhenObjectPropertiesChangeOrder()
    {
        var first = Json("""{"city":"Москва","options":{"enabled":true,"days":[1,2]}}""");
        var reordered = Json("""{"options":{"days":[1,2],"enabled":true},"city":"Москва"}""");
        Assert.Equal(ToolInvocation.Operation("weather", first), ToolInvocation.Operation("weather", reordered));
    }

    [Fact]
    public void SignedContextCannotBeReusedForDifferentToolOrNestedArguments()
    {
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var authorization = new ToolRequestAuthorization(key, TimeProvider.System);
        var operation = ToolInvocation.Operation("read_private", Json("""{"options":{"enabled":true}}"""));
        var metadata = authorization.Create(42, operation);
        Assert.Equal(42, authorization.Verify(metadata, operation));
        Assert.Throws<UnauthorizedAccessException>(() => authorization.Verify(metadata,
            ToolInvocation.Operation("other_tool", Json("""{"options":{"enabled":true}}"""))));
        Assert.Throws<UnauthorizedAccessException>(() => authorization.Verify(metadata,
            ToolInvocation.Operation("read_private", Json("""{"options":{"enabled":false}}"""))));
    }
}
