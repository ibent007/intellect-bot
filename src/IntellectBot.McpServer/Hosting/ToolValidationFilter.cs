using System.Text.Json;
using System.Text.Json.Nodes;
using IntellectBot.Validation;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace IntellectBot.McpServer.Hosting;

// Единая граница всех зарегистрированных методов: имя и схема берутся из каталога SDK.
public static class ToolValidationFilter
{
    public static JsonElement InputSchema(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        // Закрываем обычную схему параметров SDK, но сохраняем явный контракт
        // расширяемого объекта и схемы, составленные через allOf/anyOf.
        if (node.ContainsKey("properties") && !node.ContainsKey("additionalProperties"))
            node["additionalProperties"] = false;
        return JsonSerializer.SerializeToElement(node);
    }

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, ct) =>
    {
        var collection = context.Server.ServerOptions.ToolCollection;
        if (collection is null || !collection.TryGetPrimitive(context.Params.Name, out var method))
            throw new McpException("Неизвестный инструмент.");
        var definition = method.ProtocolTool;
        var arguments = JsonSerializer.SerializeToElement(context.Params.Arguments ?? new Dictionary<string, JsonElement>());
        if (!JsonSchemaValidator.Matches(arguments, InputSchema(definition.InputSchema)))
            throw new McpException("Аргументы не соответствуют схеме инструмента.");
        var result = await next(context, ct);
        if (result.IsError != true && definition.OutputSchema is { } schema &&
            (result.StructuredContent is not { } output || !JsonSchemaValidator.Matches(output, schema)))
            throw new McpException("Результат инструмента не соответствует его схеме.");
        return result;
    };

    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> Describe(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) => async (context, ct) =>
    {
        var result = await next(context, ct);
        foreach (var tool in result.Tools)
            tool.InputSchema = InputSchema(tool.InputSchema);
        return result;
    };
}
