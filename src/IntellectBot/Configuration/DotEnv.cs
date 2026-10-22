using System.Collections;
using System.Text;

namespace IntellectBot.Configuration;

public static class DotEnv
{
    // Подстановок ${...} нет. Окружение процесса приоритетнее, включая пустые значения.
    public static Dictionary<string, string> Load(string path)
    {
        var values = File.Exists(path) ? Parse(File.ReadAllText(path)) : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            values[(string)entry.Key] = (string?)entry.Value ?? "";
        return values;
    }

    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();
            var equals = line.IndexOf('=');
            if (equals < 1)
                throw new ConfigException($"Некорректная строка .env: {index + 1}.");
            var key = line[..equals].Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new ConfigException("Некорректное имя переменной .env.");
            var raw = line[(equals + 1)..].TrimStart();
            if (raw.Length > 0 && raw[0] is '\'' or '"')
            {
                var quote = raw[0];
                var value = new StringBuilder();
                var closed = false;
                for (var i = 1; ; i++)
                {
                    if (i >= raw.Length)
                    {
                        if (++index >= lines.Length)
                            throw new ConfigException("Незакрытая строка в .env.");
                        raw += "\n" + lines[index];
                    }
                    var c = raw[i];
                    if (c == quote)
                    {
                        var tail = raw[(i + 1)..].Trim();
                        if (tail.Length > 0 && !tail.StartsWith('#'))
                            throw new ConfigException("Лишний текст после значения .env.");
                        closed = true;
                        break;
                    }
                    if (c == '\\' && i + 1 < raw.Length)
                    {
                        var next = raw[i + 1];
                        if (next == quote || next == '\\')
                        {
                            value.Append(next);
                            i++;
                            continue;
                        }
                        if (quote == '"' && "nrt".Contains(next))
                        {
                            value.Append(next == 'n' ? '\n' : next == 'r' ? '\r' : '\t');
                            i++;
                            continue;
                        }
                    }
                    value.Append(c);
                }
                if (closed)
                    result[key] = value.ToString();
            }
            else
            {
                var comment = raw.IndexOf(" #", StringComparison.Ordinal);
                result[key] = (comment >= 0 ? raw[..comment] : raw).TrimEnd();
            }
        }
        return result;
    }
    public static string Quote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    // Docker Compose сохраняет обратные слеши буквально внутри одинарных кавычек.
    public static string ComposeQuote(string value) => "'" + value.Replace("'", "\\'") + "'";
    public static void PrivateWrite(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(value);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
