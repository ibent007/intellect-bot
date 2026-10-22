namespace IntellectBot.Llm;

// Особенности API сосредоточены здесь; агентный цикл одинаков для всех моделей.
internal static class ModelCapabilities
{
    public static void Apply(Dictionary<string, object?> payload, string model, string effort)
    {
        if (model.StartsWith("qwen/", StringComparison.OrdinalIgnoreCase))
        {
            payload["reasoning_effort"] = effort;
            payload["reasoning_format"] = "hidden";
        }
        else if (model.StartsWith("openai/gpt-oss-", StringComparison.OrdinalIgnoreCase))
        {
            payload["reasoning_effort"] = effort;
            payload["include_reasoning"] = false;
        }
    }
}
