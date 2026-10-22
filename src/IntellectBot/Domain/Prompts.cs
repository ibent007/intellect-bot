using System.Reflection;
namespace IntellectBot.Domain;

// Системные инструкции, встроенные в сборку, не зависят от рабочей папки.
public static class Prompts
{
    private static readonly IReadOnlyDictionary<string, string> Values = Modes.Ordered.ToDictionary(mode => mode, Load);
    public static string AgentRules { get; } = Load("agent");
    public static string For(string mode) => Values.TryGetValue(mode, out var value) ? value : throw new DialogueException("Неизвестный режим.");
    private static string Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"IntellectBot.Prompts.{name}.txt")
            ?? throw new InvalidOperationException("Не найдены системные инструкции.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
