using System.Globalization;
using IntellectBot.Domain;

namespace IntellectBot.Telegram;

public static class MenuView
{
    public const string StartText = "Добро пожаловать! Я твой AI-ассистент 🤖\n\n📚 Помогаю разобраться в программировании, проверить код и перевести текст.\n\n📖 Я учитываю недавнюю историю нашего диалога и сохраняю её после перезапуска.\n\n⚙️ В настройках можно выбрать режим и уровень креативности ассистента. Кнопка «Сброс памяти» позволяет начать новый диалог 🗑\n\n☰ Команды также доступны в меню рядом с полем ввода.";
    public const string ResetText = "Память диалога очищена.\n\nРежим и уровень креативности сохранены.\nСообщения в Telegram остались в чате.\n\nМожно начать новый диалог.";
    public const string UnsupportedText = "Я пока что принимаю только текстовые сообщения.";
    public const string EmptyText = "Напишите непустой текстовый вопрос.";
    public const string UnknownText = "Неизвестная команда. Используйте команду /start.";
    public static string TranslationHint(string mode) => mode == "translate" ? "Язык оригинала: определяется автоматически.\nЯзык перевода: укажите в каждом сообщении.\nНапример: «На немецкий: Доброе утро».\n\nБез указания языка: русский → английский, английский → русский.\nДля остальных языков бот уточнит направление.\n\n" : "";
    public static string ButtonTemperature(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
    public static string Temperature(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
    public static string ModelName(string value) => value switch { "qwen/qwen3.8-27b" => "Qwen 3.8 27B", "openai/gpt-oss-120b" => "GPT-OSS 120B", _ => value };
    public static string SettingsText(DialogueSettings settings) =>
        $"Режим: {Modes.Name(settings.Mode)}\nМодель: {ModelName(settings.Model)}\nУровень креативности: {Temperature(settings.Temperature)}\n\n{TranslationHint(settings.Mode)}Выберите уровень креативности AI-ассистента кнопкой ниже (Меньшие значения обычно дают более сдержанные ответы, большие — более разнообразные).\n\nВыбор режима очищает память диалога.";
    public static string ModeText(string mode) => $"Режим: {Modes.Name(mode)}\n\n{TranslationHint(mode)}Память диалога очищена.\nУровень креативности сохранён.\nОтправьте новое сообщение.\n\nВыбор другого режима также очищает память диалога.";
    public static InlineKeyboard MainKeyboard() => new([new InlineButton[] { new("🗑 Сброс памяти", "menu:reset"), new("⚙️ Настройки", "menu:settings") }]);
    public static InlineKeyboard BackKeyboard() => new([new InlineButton[] { new("⬅️ Меню", "menu:home") }]);
    public static InlineKeyboard SettingsKeyboard(DialogueSettings settings, bool showBack)
    {
        var prefix = showBack ? "menu" : "settings";
        List<IReadOnlyList<InlineButton>> rows = [Modes.Temperatures.Select(value => new InlineButton((value == settings.Temperature ? "✓ " : "") + ButtonTemperature(value), $"{prefix}:temperature:{ButtonTemperature(value)}")).ToArray(),
            Modes.Ordered.Where(mode => mode != settings.Mode).Select(mode => new InlineButton((mode switch { "study" => "📚 ", "review" => "🔎 ", _ => "🌐 " }) + Modes.Name(mode), $"{prefix}:{mode}")).ToArray()];
        if (showBack)
            rows.Add(BackKeyboard().Rows[0]);
        return new(rows);
    }
}
