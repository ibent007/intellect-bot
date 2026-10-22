using System.Globalization;
using IntellectBot.Domain;

namespace IntellectBot.Telegram;

public static class MenuView
{
    public const string StartText = "Привет! Я intellect 🤖\n\nПомогу разобраться в сложном, написать код, перевести текст, узнать погоду и ваше расписание. Просто напишите вопрос.\n\nЧтобы очистить память предыдущего диалога, нажмите «Новый чат».";
    public const string ResetText = "Память диалога очищена.\n\nНастройки, расписание и напоминания сохранены. Сообщения в Telegram остались в чате.\n\nМожно начать новый диалог.";
    public const string HelpText = "Что я умею\n\n• Объяснять сложное простыми словами\n• Писать, разбирать и исправлять код\n• Переводить тексты\n• Узнавать погоду и ваше расписание\n• Сохранять напоминания после подтверждения\n\nПросто напишите, что нужно — переключать режим не требуется. Расписание и часовой пояс можно настроить в «Настройках».\n\nКнопка «Показать инструменты» расскажет, откуда я получаю данные и какие действия могу выполнить.\n\nУведомления о напоминаниях пока не отправляются.";
    public const string UnsupportedText = "Я пока что принимаю только текстовые сообщения.";
    public const string EmptyText = "Напишите непустой текстовый вопрос.";
    public const string UnknownText = "Неизвестная команда. Используйте команду /start.";
    public static string TranslationHint(string mode) => mode == "translate" ? "Язык оригинала: определяется автоматически.\nЯзык перевода: укажите в каждом сообщении.\nНапример: «На немецкий: Доброе утро».\n\nБез указания языка: русский → английский, английский → русский.\nДля остальных языков бот уточнит направление.\n\n" : "";
    public static string ButtonTemperature(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
    public static string Temperature(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
    public static string ModelName(string value) => LlmModels.Name(value);
    public static string SettingsText(DialogueSettings settings, string? zone = null) =>
        $"Настройки\n\nМодель: {ModelName(settings.Model)}\nУровень креативности: {Temperature(settings.Temperature)}\nЧасовой пояс: {zone ?? "не задан"}\n\nВыберите креативность: меньшие значения дают более сдержанные ответы, большие — более разнообразные.";
    public static InlineKeyboard MainKeyboard() => new([
        new InlineButton[]{new("Новый чат","menu:reset"),new("Возможности","menu:help")},
        new InlineButton[]{new("Настройки","menu:settings")}
    ]);
    public static InlineKeyboard BackKeyboard() => new([new InlineButton[] { new("Назад", "menu:home") }]);
    public static InlineKeyboard HelpKeyboard() => new([
        new InlineButton[] { new("Показать инструменты", "menu:tools") },
        new InlineButton[] { new("Назад", "menu:home") }
    ]);
    public static InlineKeyboard ToolsKeyboard() => new([
        new InlineButton[] { new("Назад", "menu:help") }
    ]);
    public static InlineKeyboard SettingsKeyboard(DialogueSettings settings, bool showBack)
    {
        var prefix = showBack ? "menu" : "settings";

        List<IReadOnlyList<InlineButton>> rows =
        [
            Modes.Temperatures.Select(value => new InlineButton(
                (value == settings.Temperature ? "✓ " : "") +
                    ButtonTemperature(value),
                $"{prefix}:temperature:{ButtonTemperature(value)}")).ToArray(),

            new InlineButton[]
            {
                new("Часовой пояс", $"{prefix}:zone"),
                new("Расписание", $"{prefix}:schedule")
            },

            new InlineButton[]
            {
                new("Выбрать модель", $"{prefix}:models")
            }
        ];

        if (showBack)
            rows.Add(BackKeyboard().Rows[0]);

        return new(rows);
    }
    public static readonly (string City, string Zone)[] TimeZones =
    [
        ("Москва", "Europe/Moscow"), ("Таллин", "Europe/Tallinn"),
        ("Берлин", "Europe/Berlin"), ("Лондон", "Europe/London"),
        ("Тбилиси", "Asia/Tbilisi"), ("Дубай", "Asia/Dubai"),
        ("Токио", "Asia/Tokyo"), ("Нью-Йорк", "America/New_York")
    ];

    public static InlineKeyboard TimeZoneKeyboard(bool showBack, string? selectedZone = null)
    {
        var prefix = showBack ? "menu" : "settings";

        var rows = TimeZones.Chunk(2)
            .Select(pair => (IReadOnlyList<InlineButton>)pair.Select(item =>
                new InlineButton(
                    (item.Zone == selectedZone ? "✓ " : "") + item.City,
                    $"{prefix}:tz:{item.Zone}")).ToArray())
            .ToList();

        rows.Add(new InlineButton[]
        {
            new("Назад", $"{prefix}:settings"),
            new("Другой пояс", $"{prefix}:zone_custom")
        });

        return new(rows);
    }

    public static InlineKeyboard ModelKeyboard(DialogueSettings settings, bool showBack)
    {
        var prefix = showBack ? "menu" : "settings";

        var rows = LlmModels.Available.Select(model =>
            (IReadOnlyList<InlineButton>)new InlineButton[]
            {
                new(
                    (model == settings.Model ? "✓ " : "") + ModelName(model),
                    $"{prefix}:model:{model}")
            }).ToList();

        rows.Add(new InlineButton[]
        {
            new("Назад", $"{prefix}:settings")
        });

        return new(rows);
    }
}
