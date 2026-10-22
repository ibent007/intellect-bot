using System.Text.RegularExpressions;

namespace IntellectBot.Agent;

// Отсеивает обрывок, который просто повторяет слова запроса вместо ответа.
public static class AgentAnswerPolicy
{
    public static bool IsEcho(string question, string answer)
    {
        static string[] Words(string value) => Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(match => match.Value).ToArray();
        var response = Words(answer).Where(word => word != "00").ToArray();
        if (response.Length is 0 or > 8 || answer.Contains('`') || response.All(word => word.All(char.IsDigit)))
            return false;
        if (response.Length == 1 && response[0] is "привет" or "здравствуйте" or "спасибо" or "да" or "нет")
            return false;
        var input = Words(question).ToHashSet(StringComparer.Ordinal);
        return response.All(input.Contains);
    }
    public static bool AsksConfirmation(string text) => Regex.IsMatch(text,
        @"подтвержда(?:ете|ешь)|подтверди(?:те|ть)|создать.{0,30}напоминание\?|напоминание.{0,15}(?:сохранено|создано|установлено)|напомню", RegexOptions.IgnoreCase);

    public static bool NeedsReference(string question)
    {
        if (Regex.IsMatch(question, @"переведи|перевод|напомн|погод|расписан|\bкод\b|SOLID|GRASP|C#|C\+\+", RegexOptions.IgnoreCase))
            return false;
        return Regex.IsMatch(question,
            @"кто\s+(?:такой|такая|такие|основал|создал)|сколько\s+лет|(?:ссылк|канал).{0,35}(?:телеграм|telegram)|(?:телеграм|telegram).{0,35}(?:ссылк|канал)|биограф|дата\s+рожд|брат\s+|сестра\s+|за\s+тип(?:ок|а)|основател[ьи]", RegexOptions.IgnoreCase);
    }

    public static bool HasMixedScriptWords(string answer)
    {
        var prose = Regex.Replace(answer, @"```[\s\S]*?(?:```|\z)|`[^`\r\n]*`", "");
        return Regex.Matches(prose, @"\p{L}+").Any(word =>
            Regex.IsMatch(word.Value, @"[А-Яа-яЁё]") && Regex.IsMatch(word.Value, @"[A-Za-z\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]"));
    }
    public static string? RequiredEvidence(string question)
    {
        if (NeedsReference(question))
            return "reference";
        return Regex.IsMatch(question, @"погод[а-яё]*\s+(?:в|на)|какая\s+погода|нужен.{0,20}зонт|сколько.{0,20}градус", RegexOptions.IgnoreCase)
            ? "weather" : null;
    }
    public static bool IsAcknowledgement(string question) => Regex.IsMatch(question.Trim(),
        @"^(?:спасибо|понятно|хорошо|ок|да|нет|отлично)[.!]*$", RegexOptions.IgnoreCase);
}
