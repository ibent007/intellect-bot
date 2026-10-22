using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Net;

namespace IntellectBot.Validation;

// Общий валидатор схем MCP: одинаково проверяет аргументы и результаты.
// Никаких имён инструментов или сетевой загрузки $ref здесь нет.
public static class JsonSchemaValidator
{
    public static bool IsSchema(JsonElement schema) => schema.ValueKind is
        JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False;

    public static bool Matches(JsonElement value, JsonElement schema)
    {
        try
        {
            var budget = 50000;
            var evaluation = new EvaluationBudget();
            return HasUniqueKeys(value, 0, ref budget) && Check(value, schema, schema, 0, evaluation) && evaluation.Remaining >= 0;
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool HasUniqueKeys(JsonElement value, int depth, ref int budget)
    {
        if (depth > 64 || --budget < 0)
            return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !HasUniqueKeys(property.Value, depth + 1, ref budget))
                    return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (!HasUniqueKeys(item, depth + 1, ref budget))
                    return false;
        return true;
    }

    private sealed class EvaluationBudget
    {
        public int Remaining = 50000;
    }

    private static bool Check(JsonElement value, JsonElement schema, JsonElement root, int depth, EvaluationBudget budget)
    {
        if (depth > 64 || --budget.Remaining < 0)
            return false;
        if (schema.ValueKind == JsonValueKind.True)
            return true;
        if (schema.ValueKind == JsonValueKind.False || schema.ValueKind != JsonValueKind.Object)
            return false;
        // Эти словари требуют отслеживания оценённых полей или динамических областей.
        // Не игнорируем неизвестное ограничение и не объявляем данные проверенными.
        if (schema.TryGetProperty("unevaluatedProperties", out _) || schema.TryGetProperty("unevaluatedItems", out _) ||
            schema.TryGetProperty("$dynamicRef", out _) || schema.TryGetProperty("$recursiveRef", out _))
            return false;
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var path = reference.GetString()!;
            if (path != "#" && !path.StartsWith("#/", StringComparison.Ordinal))
                return false;
            var target = root;
            if (path != "#")
                foreach (var segment in path[2..].Split('/'))
                    target = target.GetProperty(Uri.UnescapeDataString(segment).Replace("~1", "/").Replace("~0", "~"));
            if (!Check(value, target, root, depth + 1, budget))
                return false;
        }
        if (schema.TryGetProperty("anyOf", out var any) &&
            !any.EnumerateArray().Any(branch => Check(value, branch, root, depth + 1, budget)))
            return false;
        if (schema.TryGetProperty("oneOf", out var one) &&
            one.EnumerateArray().Count(branch => Check(value, branch, root, depth + 1, budget)) != 1)
            return false;
        if (schema.TryGetProperty("allOf", out var all) &&
            !all.EnumerateArray().All(branch => Check(value, branch, root, depth + 1, budget)))
            return false;
        if (schema.TryGetProperty("not", out var negative) && Check(value, negative, root, depth + 1, budget))
            return false;
        if (schema.TryGetProperty("if", out var condition))
        {
            var branchName = Check(value, condition, root, depth + 1, budget) ? "then" : "else";
            if (schema.TryGetProperty(branchName, out var branch) && !Check(value, branch, root, depth + 1, budget))
                return false;
        }
        if (schema.TryGetProperty("enum", out var values) &&
            !values.EnumerateArray().Any(option => JsonElement.DeepEquals(value, option)))
            return false;
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(value, constant))
            return false;
        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(item => item.GetString()) : new[] { type.GetString() };
            if (!types.Any(name => TypeMatches(value, name)))
                return false;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = value.EnumerateObject().Select(property => property.Name).ToArray();
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
                return false;
            if (schema.TryGetProperty("minProperties", out var minProperties) && keys.Length < minProperties.GetInt32() ||
                schema.TryGetProperty("maxProperties", out var maxProperties) && keys.Length > maxProperties.GetInt32())
                return false;
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray())
                    if (!value.TryGetProperty(name.GetString()!, out _))
                        return false;
            schema.TryGetProperty("properties", out var properties);
            schema.TryGetProperty("patternProperties", out var patterns);
            foreach (var property in value.EnumerateObject())
            {
                var covered = false;
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    covered = true;
                    if (!Check(property.Value, propertySchema, root, depth + 1, budget))
                        return false;
                }
                if (patterns.ValueKind == JsonValueKind.Object)
                    foreach (var pattern in patterns.EnumerateObject())
                        if (Regex.IsMatch(property.Name, pattern.Name, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                        {
                            covered = true;
                            if (!Check(property.Value, pattern.Value, root, depth + 1, budget))
                                return false;
                        }
                if (!covered && schema.TryGetProperty("additionalProperties", out var additional) &&
                    !Check(property.Value, additional, root, depth + 1, budget))
                    return false;
                if (schema.TryGetProperty("propertyNames", out var nameSchema) &&
                    !Check(JsonSerializer.SerializeToElement(property.Name), nameSchema, root, depth + 1, budget))
                    return false;
            }
            if (schema.TryGetProperty("dependentRequired", out var dependencies))
                foreach (var dependency in dependencies.EnumerateObject())
                    if (value.TryGetProperty(dependency.Name, out _) &&
                        dependency.Value.EnumerateArray().Any(key => !value.TryGetProperty(key.GetString()!, out _)))
                        return false;
            if (schema.TryGetProperty("dependentSchemas", out var dependentSchemas))
                foreach (var dependency in dependentSchemas.EnumerateObject())
                    if (value.TryGetProperty(dependency.Name, out _) && !Check(value, dependency.Value, root, depth + 1, budget))
                        return false;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var length = value.GetArrayLength();
            if (schema.TryGetProperty("minItems", out var min) && length < min.GetInt32() ||
                schema.TryGetProperty("maxItems", out var max) && length > max.GetInt32())
                return false;
            var prefixCount = 0;
            if (schema.TryGetProperty("prefixItems", out var prefix))
            {
                prefixCount = prefix.GetArrayLength();
                for (var i = 0; i < Math.Min(length, prefixCount); i++)
                    if (!Check(value[i], prefix[i], root, depth + 1, budget))
                        return false;
            }
            if (schema.TryGetProperty("items", out var itemSchema))
                foreach (var item in value.EnumerateArray().Skip(prefixCount))
                    if (!Check(item, itemSchema, root, depth + 1, budget))
                        return false;
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True)
                for (var i = 0; i < length; i++)
                    for (var j = i + 1; j < length; j++)
                        if (JsonElement.DeepEquals(value[i], value[j]))
                            return false;
            if (schema.TryGetProperty("contains", out var contains))
            {
                var count = value.EnumerateArray().Count(item => Check(item, contains, root, depth + 1, budget));
                if (count < (schema.TryGetProperty("minContains", out var minimum) ? minimum.GetInt32() : 1) ||
                    schema.TryGetProperty("maxContains", out var maximum) && count > maximum.GetInt32())
                    return false;
            }
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var length = value.GetString()!.EnumerateRunes().Count();
            if (schema.TryGetProperty("minLength", out var min) && length < min.GetInt32() ||
                schema.TryGetProperty("maxLength", out var max) && length > max.GetInt32())
                return false;
            if (schema.TryGetProperty("pattern", out var pattern) &&
                !Regex.IsMatch(value.GetString()!, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                return false;
            if (schema.TryGetProperty("format", out var format) && !FormatMatches(value.GetString()!, format.GetString()))
                return false;
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                return false;
            if (schema.TryGetProperty("exclusiveMinimum", out var exclusiveMin) && number <= exclusiveMin.GetDouble() ||
                schema.TryGetProperty("exclusiveMaximum", out var exclusiveMax) && number >= exclusiveMax.GetDouble())
                return false;
            if (schema.TryGetProperty("multipleOf", out var multiple))
            {
                var divisor = multiple.GetDouble();
                if (divisor <= 0 || !double.IsFinite(number / divisor) ||
                    Math.Abs(number / divisor - Math.Round(number / divisor)) > 1e-9)
                    return false;
            }
            if (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble() ||
                schema.TryGetProperty("maximum", out var max) && number > max.GetDouble())
                return false;
        }
        return true;
    }

    // Неизвестный format является аннотацией JSON Schema, а не новым типом.
    private static bool FormatMatches(string value, string? format) => format switch
    {
        "date" => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "time" => Regex.IsMatch(value, @"^\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) &&
            DateTimeOffset.TryParse("2000-01-01T" + value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "date-time" => Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "uuid" => Guid.TryParseExact(value, "D", out _),
        "uri" => Uri.TryCreate(value, UriKind.Absolute, out _),
        "uri-reference" => Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _),
        "ipv4" => IPAddress.TryParse(value, out var ip4) && ip4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork,
        "ipv6" => IPAddress.TryParse(value, out var ip6) && ip6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
        _ => true
    };
    private static bool TypeMatches(JsonElement value, string? name) => name switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "null" => value.ValueKind == JsonValueKind.Null,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n) && decimal.Truncate(n) == n,
        _ => false
    };
}
