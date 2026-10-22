using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace IntellectBot.McpServer.Weather;

[McpServerToolType]
public sealed class WeatherEndpoints(WeatherService weather)
{
    [McpServerTool(
        Name = "get_weather",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Получает текущую погоду по расчётам Open-Meteo: температуру в °C, " +
        "ветер в м/с и время данных. Если город найден неоднозначно, " +
        "возвращает candidates с названиями и регионами. Модель выбирает различимые " +
        "варианты для уточнения; одинаковые подписи и координаты пользователю не показываются.")]
    public async Task<WeatherResult> GetWeather(
        [Description(
            "Город: от 1 до 100 символов после удаления пробелов по краям. " +
            "Страну, область и район добавляй только если их указал пользователь или вернул источник. " +
            "Не угадывай страну. Питер и СПб означают Санкт-Петербург; передай название без выдуманной страны. " +
            "Используй названия регионов из возвращённого списка кандидатов.")]
        string city,
        CancellationToken cancellationToken,
        [Description(
            "ID точного места из candidates предыдущего результата MCP. " +
            "Используй при выборе пользователя или единственном различимом месте. Не придумывай ID.")]
        string? locationId = null)
    {
        try
        {
            return await weather.GetAsync(city, cancellationToken, locationId);
        }
        catch (ArgumentException)
        {
            throw new McpException(
                "Укажите город: от 1 до 100 символов. " +
                "Можно уточнить страну и регион через запятую.");
        }
        catch (WeatherException exception)
        {
            throw new McpException(exception.Message);
        }
    }
}
