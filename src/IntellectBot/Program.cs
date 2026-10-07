using IntellectBot.Configuration;
using IntellectBot.Hosting;
using IntellectBot.Infrastructure;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
try
{
    var arguments = args.ToList();
    var envFile = ".env";
    var envIndex = arguments.IndexOf("--env-file");
    if (envIndex >= 0)
    {
        if (envIndex + 1 >= arguments.Count)
            throw new ConfigException("--env-file: укажите путь.");
        envFile = arguments[envIndex + 1];
        arguments.RemoveRange(envIndex, 2);
    }
    return arguments.FirstOrDefault() switch
    {
        "healthcheck" => await BotHost.HealthcheckAsync(cancellation.Token, envFile),
        "local" => await LocalCommands.RunAsync(arguments.Skip(1).ToArray(), cancellation.Token),
        "cloud" => await CloudCommands.RunAsync(arguments.Skip(1).ToArray(), cancellation.Token),
        "dbcheck" => await LocalCommands.DatabaseCheckAsync(cancellation.Token, envFile),
        "help" or "--help" or "-h" => Help(),
        null or "run" => await BotHost.RunAsync(AppSettings.Load(envFile), cancellation.Token),
        _ => throw new ConfigException("Неизвестная команда. Используйте --help.")
    };
}
catch (OperationCanceledException) { return 130; }
catch (Exception error) when (error is ConfigException or CommandException) { Console.Error.WriteLine(error.Message); return 1; }
catch (Exception) { Console.Error.WriteLine("Операция не выполнена. Проверьте файлы, конфигурацию и сеть по README."); return 1; }
static int Help()
{
    Console.WriteLine("intellect_bot: run | healthcheck | dbcheck | local [up|stop|status|logs|health] [--setup-only] | cloud [up|status|logs|stop|start|access|destroy].\nЗапускайте из корня репозитория. Инструкции: README.md.");
    return 0;
}
