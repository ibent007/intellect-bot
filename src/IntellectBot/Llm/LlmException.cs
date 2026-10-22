using IntellectBot.Domain;

namespace IntellectBot.Llm;

public sealed class LlmException(string code) : Exception(MessageFor(code))
{
    public string Code { get; } = code;
    public static string MessageFor(string code) => code switch
    {
        "timeout" => "Модель не успела ответить. Попробуйте ещё раз чуть позже.",
        "authorization" => "Не удалось подключиться к сервису ИИ. Сообщите администратору.",
        "rate_limit" => "API ограничил этот запрос по частоте или объёму токенов. Повторите чуть позже либо выберите другую модель в настройках.",
        "invalid_response" => "Модель вернула некорректный ответ. Попробуйте ещё раз.",
        "empty_response" => "Ответ модели пуст. Попробуйте переформулировать вопрос.",
        "unhelpful_response" => "Модель не смогла сформировать содержательный ответ. Уточните запрос; действие не выполнялось без подтверждения.",
        "incomplete_response" => "Ответ модели не завершён. Попробуйте задать более узкий вопрос.",
        "invalid_temperature" => Modes.InvalidTemperature,
        "invalid_translation" => "Модель не смогла оформить перевод. Повторите запрос с указанием языка.",
        "invalid_language" => "Модель вернула некорректный текст. Повторите запрос или выберите другую модель.",
        "invalid_clarification" => "Модель не смогла сформулировать уточнение. Укажите недостающие параметры явно.",
        "request_rejected" => "Сервис ИИ отклонил запрос. Попробуйте другой запрос или модель.",
        _ => "Сервис ИИ временно недоступен. Попробуйте позже."
    };
    public string? ApiCode
    {
        get; init;
    }
    public int? HttpStatus
    {
        get; init;
    }
    public TimeSpan? RetryAfter
    {
        get; init;
    }
}
