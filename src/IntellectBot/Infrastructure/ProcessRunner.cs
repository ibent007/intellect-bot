using System.Diagnostics;

namespace IntellectBot.Infrastructure;

public sealed class CommandException(string message) : Exception(message);
public static class ProcessRunner
{
    public static async Task<string> RunAsync(string file, IEnumerable<string> arguments, CancellationToken ct, string? input = null,
        IReadOnlyDictionary<string, string>? environment = null, int timeoutSeconds = 600, bool interactive = false)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = !interactive, RedirectStandardError = !interactive, RedirectStandardInput = input is not null };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            process.Start();
            var output = interactive ? Task.FromResult("") : process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = interactive ? Task.FromResult("") : process.StandardError.ReadToEndAsync(timeout.Token);
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0)
                throw new CommandException($"Команда {Path.GetFileName(file)} не выполнена (код {process.ExitCode}): {await error}");
            return await output;
        }
        catch (System.ComponentModel.Win32Exception) { throw new CommandException($"Не удалось запустить {Path.GetFileName(file)}. Проверьте установку и PATH."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CommandException($"Истёк таймаут {Path.GetFileName(file)}."); }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }
}
