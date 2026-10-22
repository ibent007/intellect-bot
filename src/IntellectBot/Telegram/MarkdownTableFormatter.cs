using System.Text;
using System.Text.RegularExpressions;

namespace IntellectBot.Telegram;

// Telegram не поддерживает Markdown-таблицы. В обычном тексте превращаем строки в карточки.
// Код заранее защищён MessageFormatter и сюда не попадает.
internal static class MarkdownTableFormatter
{
    public static string Convert(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var headers = Cells(lines[i]);
            var separator = i + 1 < lines.Length ? Cells(lines[i + 1]) : [];
            if (headers.Length < 2 || separator.Length != headers.Length ||
                !separator.All(value => Regex.IsMatch(value, @"^:?-{3,}:?$")))
            {
                result.Add(lines[i]);
                continue;
            }
            i += 2;
            while (i < lines.Length)
            {
                var values = Cells(lines[i]);
                if (values.Length != headers.Length)
                    break;
                result.Add("• " + values[0]);
                for (var column = 1; column < headers.Length; column++)
                    result.Add("  " + headers[column] + ": " + values[column]);
                result.Add("");
                i++;
            }
            i--;
        }
        return string.Join('\n', result);
    }

    private static string[] Cells(string line)
    {
        if (!line.Contains('|'))
            return [];
        var cells = new List<string>();
        var value = new StringBuilder();
        var escaped = false;
        var row = line.Trim();
        if (row.StartsWith('|'))
            row = row[1..];
        if (row.EndsWith('|'))
        {
            var slashes = 0;
            for (var i = row.Length - 2; i >= 0 && row[i] == '\\'; i--)
                slashes++;
            if (slashes % 2 == 0)
                row = row[..^1];
        }
        foreach (var ch in row)
        {
            if (escaped)
            {
                value.Append(ch);
                escaped = false;
            }
            else if (ch == '\\')
                escaped = true;
            else if (ch == '|')
            {
                cells.Add(value.ToString().Trim());
                value.Clear();
            }
            else
                value.Append(ch);
        }
        if (escaped)
            value.Append('\\');
        cells.Add(value.ToString().Trim());
        return cells.ToArray();
    }
}
