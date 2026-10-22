namespace IntellectBot.Configuration;

// Параметры генерации и ограничения сетевой границы LLM.
public sealed class LlmSettings
{
    public required string BaseUrl
    {
        get; init;
    }
    public required string ApiKey
    {
        get; init;
    }
    public required string Model
    {
        get; init;
    }
    public string ReasoningEffort { get; init; } = "low";
    public int TimeoutSeconds { get; init; } = 60;
    public int MaxCompletionTokens { get; init; } = 4096;
    public int ToolResponseMaxTokens { get; init; } = 1024;
    public int ToolContextMaxChars { get; init; } = 12000;
    public int MaxRateWaitSeconds { get; init; } = 5;
    public int HistoryMaxMessages { get; init; } = 12;
    public int ContextMaxChars { get; init; } = 6000;
}
