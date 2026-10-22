using System.Reflection;
using System.Text;

namespace IntellectBot.Domain;

public static class ContextBuilder
{
    public static int Length(string text) => text.EnumerateRunes().Count();
    public static IReadOnlyList<ChatMessage> Build(string mode, IReadOnlyList<ChatMessage> history, string question, int maxMessages, int maxChars)
    {
        var prompt = Prompts.For(mode);
        var required = Length(prompt) + Length(question);
        if (required > maxChars)
            throw new DialogueException("Вопрос слишком длинный. Сократите его и отправьте ещё раз.");
        var count = Math.Max(0, maxMessages / 2 * 2);
        var selected = history.Skip(Math.Max(0, history.Count - count))
            .Select(message => message.Role == "assistant" && message.Content.Length > 800
                ? message with
                {
                    Content = message.Content[..800] + "\n[Предыдущий ответ сокращён только для контекста.]"
                } : message).ToList();
        while (selected.Count > 0 && required + selected.Sum(m => Length(m.Content)) > maxChars)
            selected.RemoveRange(0, Math.Min(2, selected.Count));
        return [new("system", prompt), .. selected, new("user", question)];
    }
}
