namespace IntellectBot.Infrastructure;

// Замена файла после полной записи защищает отчёты от частичного JSON при остановке.
public static class AtomicFile
{
    public static async Task WriteTextAsync(string path, string content, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, ct);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
