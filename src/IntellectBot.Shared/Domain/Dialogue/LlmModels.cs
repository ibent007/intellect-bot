namespace IntellectBot.Domain;

// Общий список моделей для интерфейса, проверки и запросов API.
public static class LlmModels
{
    public const string Qwen = "qwen/qwen3.8-27b";
    public const string GptOss = "openai/gpt-oss-120b";

    public static IReadOnlyList<string> Available
    {
        get;
    } =
        Array.AsReadOnly(new[] { Qwen, GptOss });

    public static bool IsAllowed(string model) => Available.Contains(model);

    public static string Name(string model) => model switch
    {
        Qwen => "Qwen 3.8 27B",
        GptOss => "GPT-OSS 120B",
        _ => model
    };

    public static string Resolve(string? selected, string fallback)
    {
        if (selected is null)
            return fallback;

        if (!IsAllowed(selected))
            throw new ArgumentException(
                "Выберите модель из списка в настройках.");

        return selected;
    }
}
