
namespace IntellectBot.Domain;

// Старые ключи нужны для совместимости данных и проверки миграций. Рабочий диалог всегда использует chat.
public static class Modes
{
    public static readonly string[] Ordered = ["chat", "study", "review", "translate"];
    public static readonly double[] Temperatures = [0.0, 0.3, 0.7, 1.0];
    public const string InvalidTemperature = "Допустимые значения уровня креативности: 0.0, 0.3, 0.7, 1.0.";
    public static string Name(string mode) => mode switch { "chat" => "Общение", "study" => "Обучение", "review" => "Проверка кода", "translate" => "Перевод", _ => throw new DialogueException("Неизвестный режим. Выберите режим в настройках.") };
    public static void ValidateTemperature(double value)
    {
        if (!Temperatures.Contains(value))
            throw new DialogueException(InvalidTemperature);
    }
}
