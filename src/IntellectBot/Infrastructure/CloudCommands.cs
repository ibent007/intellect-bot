using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntellectBot.Configuration;

namespace IntellectBot.Infrastructure;

// Cloud operations are invoked only by explicit `cloud` commands, never on bot startup.
public sealed class CloudCommands
{
    private const string Remote = "/home/student/course-bot";
    private const string Compose = "cd /home/student/course-bot && docker compose --project-name intellect-csharp --env-file .env.cloud --profile cloud";
    private static readonly Dictionary<string, string[]> Kinds = new() { ["network"] = ["vpc", "network"], ["subnet"] = ["vpc", "subnet"], ["security-group"] = ["vpc", "security-group"], ["vm"] = ["compute", "instance"] };
    private readonly string directory = Path.Combine(LocalCommands.Root, ".deploy");
    private readonly string yc;
    private readonly string folder;
    private readonly CancellationToken ct;
    private readonly JsonObject state;
    private JsonObject Resources => (JsonObject)state["resources"]!;
    private string Owner => state["owner"]!.GetValue<string>();
    private string StatePath => Path.Combine(directory, "state.json");
    private CloudCommands(string ycPath, string folderId, CancellationToken cancellation)
    {
        yc = ycPath;
        folder = folderId;
        ct = cancellation;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        state = File.Exists(StatePath) ? JsonNode.Parse(File.ReadAllText(StatePath))!.AsObject() : new JsonObject { ["version"] = 1, ["implementation"] = "csharp", ["folder_id"] = folder, ["owner"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), ["resources"] = new JsonObject() };
        if (state["implementation"]?.GetValue<string>() != "csharp")
            throw new CommandException("Несовместимый формат состояния деплоя. Используйте состояние этого приложения.");
        if (state["folder_id"]?.GetValue<string>() != folder)
            throw new CommandException("Состояние деплоя относится к другому каталогу Yandex Cloud.");
        Save();
    }
    private void Save()
    {
        var temporary = StatePath + ".tmp";
        DotEnv.PrivateWrite(temporary, state.ToJsonString(new()
        {
            WriteIndented = true
        }) + "\n");
        File.Move(temporary, StatePath, true);
    }
    private async Task<JsonNode> Yc(params string[] args)
    {
        var result = await ProcessRunner.RunAsync(yc, [.. args, "--folder-id", folder, "--format", "json"], ct);
        return string.IsNullOrWhiteSpace(result) ? new JsonObject() : JsonNode.Parse(result)!;
    }
    private void Verify(JsonNode resource)
    {
        if (resource["labels"]?["template-id"]?.GetValue<string>() != Owner || resource["folder_id"]?.GetValue<string>() != folder)
            throw new CommandException("Ресурс не принадлежит этому проекту; операция отменена.");
    }
    private async Task<JsonNode?> Lookup(string key)
    {
        if (Resources[key] is not JsonObject record)
            return null;
        var list = (await Yc([.. Kinds[key], "list"])).AsArray();
        var id = record["id"]?.GetValue<string>();
        var matches = list.Where(r => r is not null && (id is not null ? r["id"]?.GetValue<string>() == id : r["name"]?.GetValue<string>() == record["name"]?.GetValue<string>())).ToArray();
        if (matches.Length > 1)
            throw new CommandException("Найдено несколько одноимённых ресурсов. Проверьте консоль облака.");
        if (matches.Length == 0)
            return null;
        var resource = matches[0]!;
        if (record["managed"]?.GetValue<bool>() != false)
            Verify(resource);
        else if (resource["folder_id"]?.GetValue<string>() != folder)
            throw new CommandException("Внешняя сеть относится к другому каталогу.");
        return resource;
    }
    private async Task<JsonNode> Ensure(string key, params string[] flags)
    {
        var name = $"intellect-{Owner}-{key}";
        Resources[key] ??= new JsonObject { ["name"] = name };
        Save(); // Record intent before remote creation; recover by name after an ambiguous timeout.
        var resource = await Lookup(key);
        if (resource is null)
        {
            if (Resources[key]!["id"] is not null)
                throw new CommandException($"Ранее созданный {key} исчез. Проверьте облако перед повторным созданием.");
            Console.WriteLine($"Создаём ресурс: {key}…");
            resource = await Yc([.. Kinds[key], "create", "--name", name, "--labels", $"template-id={Owner}", .. flags]);
            Verify(resource);
        }
        Resources[key]!["id"] = resource["id"]!.GetValue<string>();
        Save();
        return resource;
    }
    private async Task<JsonNode> Network()
    {
        if (await Lookup("network") is { } existing)
            return existing;
        if (Resources["network"]?["id"] is not null)
            throw new CommandException("Ранее использованная сеть исчезла.");
        var defaults = (await Yc("vpc", "network", "list")).AsArray().Where(n => n?["name"]?.GetValue<string>() == "default").ToArray();
        if (defaults.Length > 1)
            throw new CommandException("Найдено несколько сетей default.");
        if (defaults.Length == 0)
            return await Ensure("network");
        var resource = defaults[0]!;
        if (resource["folder_id"]?.GetValue<string>() != folder)
            throw new CommandException("Сеть default относится к другому каталогу.");
        Resources["network"] = new JsonObject { ["id"] = resource["id"]!.GetValue<string>(), ["name"] = "default", ["managed"] = false };
        Save();
        return resource;
    }
    public static string ValidateCidr(string? value)
    {
        var parts = value?.Split('/');
        if (parts?.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !int.TryParse(parts[1], out var prefix) || prefix is < 1 or > 32)
            throw new CommandException("Укажите --ssh-cidr ВАШ_ПУБЛИЧНЫЙ_IP/32.");
        var bytes = ip.GetAddressBytes();
        var number = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var mask = uint.MaxValue << (32 - prefix);
        if ((number & ~mask) != 0)
            throw new CommandException("--ssh-cidr: адрес сети содержит ненулевые биты хоста.");
        return ip + "/" + prefix;
    }
    private async Task<JsonNode> Provision(Dictionary<string, string> options)
    {
        var defaults = new Dictionary<string, string> { ["zone"] = "ru-central1-a", ["platform"] = "standard-v2", ["cores"] = "2", ["core-fraction"] = "5", ["memory"] = "1", ["disk-size"] = "20" };
        var config = new JsonObject();
        foreach (var (key, value) in defaults)
        {
            var selected = options.GetValueOrDefault(key) ?? state["config"]?[key]?.GetValue<string>() ?? value;
            if (key is "cores" or "core-fraction" or "memory" or "disk-size" && (!int.TryParse(selected, out var number) || number < 1))
                throw new CommandException($"--{key}: требуется положительное число.");
            config[key] = selected;
        }
        if (state["config"] is { } stored && !JsonNode.DeepEquals(stored, config))
            throw new CommandException("Параметры существующей ВМ отличаются. Используйте прежние аргументы.");
        var cidr = ValidateCidr(options.GetValueOrDefault("ssh-cidr") ?? state["ssh_cidr"]?.GetValue<string>());
        if (state["ssh_cidr"] is { } old && old.GetValue<string>() != cidr)
            throw new CommandException("IP изменился. Сначала выполните cloud access --ssh-cidr НОВЫЙ_IP/32.");
        state["config"] = config;
        state["ssh_cidr"] = cidr;
        Save();
        var keyPath = Path.Combine(directory, "id_ed25519");
        if (!File.Exists(keyPath))
        {
            if (Resources["vm"] is not null)
                throw new CommandException("Утерян SSH-ключ существующей ВМ. Восстановите .deploy/id_ed25519.");
            await ProcessRunner.RunAsync("ssh-keygen", ["-t", "ed25519", "-N", "", "-f", keyPath, "-C", "intellect-" + Owner], ct);
        }
        if (!File.Exists(keyPath + ".pub"))
            throw new CommandException("Нет публичной части SSH-ключа.");
        var network = await Network();
        var subnet = await Ensure("subnet", "--network-id", network["id"]!.GetValue<string>(), "--zone", config["zone"]!.GetValue<string>(), "--range", "10.120.0.0/24");
        var security = await Ensure("security-group", "--network-id", network["id"]!.GetValue<string>(), "--rule", $"direction=ingress,protocol=tcp,port=22,v4-cidrs={cidr}", "--rule", "direction=egress,protocol=any,port=any,v4-cidrs=0.0.0.0/0");
        var init = new
        {
            groups = new[] { "docker" },
            users = new[] { new { name = "student", groups = new[] { "sudo", "docker" }, shell = "/bin/bash", sudo = "ALL=(ALL) NOPASSWD:ALL", lock_passwd = true, ssh_authorized_keys = new[] { File.ReadAllText(keyPath + ".pub").Trim() } } },
            ssh_pwauth = false,
            disable_root = true,
            package_update = true,
            packages = new[] { "docker.io", "docker-compose-v2" },
            runcmd = new[] { new[] { "bash", "-ceu", "systemctl enable --now docker\nif [ ! -f /swapfile ]; then\n fallocate -l 1G /swapfile\n chmod 600 /swapfile\n mkswap /swapfile\n swapon /swapfile\n echo '/swapfile none swap sw 0 0' >> /etc/fstab\nfi\n" } }
        };
        var initPath = Path.Combine(directory, "cloud-init.yaml");
        DotEnv.PrivateWrite(initPath, "#cloud-config\n" + JsonSerializer.Serialize(init) + "\n");
        return await Ensure("vm", "--zone", config["zone"]!.GetValue<string>(), "--platform", config["platform"]!.GetValue<string>(), "--cores", config["cores"]!.GetValue<string>(), "--core-fraction", config["core-fraction"]!.GetValue<string>(), "--memory", config["memory"]!.GetValue<string>(), "--create-boot-disk", $"image-folder-id=standard-images,image-family=ubuntu-2404-lts,type=network-hdd,size={config["disk-size"]!.GetValue<string>()},auto-delete=true", "--network-interface", $"subnet-id={subnet["id"]!.GetValue<string>()},nat-ip-version=ipv4,security-group-ids={security["id"]!.GetValue<string>()}", "--metadata-from-file", "user-data=" + initPath);
    }
    private async Task<JsonNode> Instance()
    {
        var vm = await Lookup("vm") ?? throw new CommandException("ВМ проекта не найдена. Сначала выполните cloud up.");
        var instance = await Yc("compute", "instance", "get", "--id", vm["id"]!.GetValue<string>());
        Verify(instance);
        return instance;
    }
    private Task<string> Ssh(JsonNode vm, string command, int timeout = 120, string? input = null)
    {
        var address = vm["network_interfaces"]?[0]?["primary_v4_address"]?["one_to_one_nat"]?["address"]?.GetValue<string>();
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new CommandException("У ВМ нет публичного IPv4.");
        var key = Path.Combine(directory, "id_ed25519");
        if (!File.Exists(key))
            throw new CommandException("Восстановите SSH-ключ .deploy/id_ed25519.");
        return ProcessRunner.RunAsync("ssh", ["-i", key, "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "UserKnownHostsFile=\"" + Path.Combine(directory, "known_hosts").Replace('\\', '/') + "\"", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "student@" + ip, command], ct, input: input, timeoutSeconds: timeout);
    }
    private async Task WaitSsh(JsonNode vm)
    {
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await Ssh(vm, "true", 20);
                return;
            }
            catch (CommandException) { Console.WriteLine("Ждём SSH на ВМ…"); await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        }
        throw new CommandException("SSH недоступен: проверьте --ssh-cidr, ключ и сеть.");
    }
    public static byte[] SourceArchive(string root)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            var paths = new List<string> { "Dockerfile", ".dockerignore", "compose.yaml", "Directory.Build.props", "global.json" };
            var source = Path.Combine(root, "src");
            void Visit(string directory)
            {
                foreach (var file in Directory.EnumerateFileSystemEntries(directory))
                {
                    var info = new FileInfo(file);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    if (info.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (info.Name is not ("bin" or "obj"))
                            Visit(file);
                    }
                    else if (info.Extension is ".cs" or ".csproj" or ".txt" || info.Name == "packages.lock.json")
                        paths.Add(Path.GetRelativePath(root, file));
                }
            }
            Visit(source);
            foreach (var relative in paths.Order(StringComparer.Ordinal))
            {
                var path = Path.Combine(root, relative);
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                    continue;
                using var data = File.OpenRead(path);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, relative.Replace('\\', '/')) { DataStream = data, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead });
            }
        }
        return output.ToArray();
    }
    private async Task Deploy(JsonNode vm, AppSettings settings)
    {
        await WaitSsh(vm);
        Console.WriteLine("Ждём Docker, затем собираем приложение…");
        await Ssh(vm, "sudo cloud-init status --wait && docker compose version", 900);
        await Ssh(vm, "umask 077; base64 -d > /home/student/course-source.tar.gz", 180, Convert.ToBase64String(SourceArchive(LocalCommands.Root)));
        var values = LocalCommands.Values(settings);
        values["POSTGRES_HOST"] = "db";
        values["POSTGRES_PORT"] = "5432";
        var env = string.Concat(values.Select(p => p.Key + "=" + DotEnv.ComposeQuote(p.Value) + "\n"));
        await Ssh(vm, "umask 077; base64 -d > /home/student/course-env.pending", input: Convert.ToBase64String(Encoding.UTF8.GetBytes(env)));
        var command = $"set -eu; mkdir -p {Remote}; chmod 700 {Remote}; if [ -f {Remote}/.env.cloud ]; then ({Compose} stop bot); fi; rm -rf {Remote}/src; tar -xzf /home/student/course-source.tar.gz -C {Remote}; mv /home/student/course-env.pending {Remote}/.env.cloud; chmod 600 {Remote}/.env.cloud; rm /home/student/course-source.tar.gz; {Compose} up -d --build --wait --wait-timeout 180";
        await Ssh(vm, command, 1200);
        Console.WriteLine(new SafeLog(settings).Redact(await Ssh(vm, Compose + " exec -T bot dotnet IntellectBot.dll healthcheck")));
        Console.WriteLine("Деплой завершён. Статус и логи: deploy status / deploy logs.");
    }
    private async Task Access(string cidr)
    {
        cidr = ValidateCidr(cidr);
        var group = await Lookup("security-group") ?? throw new CommandException("Группа безопасности не найдена.");
        var full = await Yc("vpc", "security-group", "get", "--id", group["id"]!.GetValue<string>());
        Verify(full);
        List<string> flags = [];
        foreach (var rule in full["rules"]?.AsArray() ?? [])
            if (rule?["direction"]?.GetValue<string>() == "INGRESS")
                flags.AddRange(["--delete-rule-id", rule["id"]!.GetValue<string>()]);
        await Yc(["vpc", "security-group", "update-rules", "--id", group["id"]!.GetValue<string>(), .. flags, "--add-rule", $"direction=ingress,protocol=tcp,port=22,v4-cidrs={cidr}"]);
        state["ssh_cidr"] = cidr;
        Save();
        Console.WriteLine("Доступ по SSH обновлён.");
    }
    private async Task Destroy()
    {
        Console.WriteLine("Будут удалены ВМ, её диск с БД и принадлежащие проекту сетевые ресурсы. Общая сеть default останется.");
        Console.Write($"Для подтверждения введите intellect-{Owner}: ");
        if (Console.ReadLine()?.Trim() != "intellect-" + Owner)
            throw new CommandException("Удаление отменено.");
        var found = new Dictionary<string, JsonNode?>();
        foreach (var key in Kinds.Keys)
            found[key] = await Lookup(key); // Validate ALL ownership before first deletion.
        foreach (var key in Kinds.Keys.Reverse())
        {
            if (found[key] is { } resource && Resources[key]?["managed"]?.GetValue<bool>() != false)
                await Yc([.. Kinds[key], "delete", "--id", resource["id"]!.GetValue<string>()]);
            Resources.Remove(key);
            Save();
        }
        File.Delete(StatePath);
        Console.WriteLine("Ресурсы проекта удалены. Облачная БД удалена вместе с диском ВМ.");
    }
    private static async Task<string> FindYc(CancellationToken ct)
    {
        try
        {
            await ProcessRunner.RunAsync("yc", ["--version"], ct);
            return "yc";
        }
        catch (CommandException) { }
        var usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "yandex-cloud", "bin", OperatingSystem.IsWindows() ? "yc.exe" : "yc");
        if (File.Exists(usual))
            return usual;
        Console.WriteLine("Устанавливаем Yandex Cloud CLI официальным установщиком…");
        var extension = OperatingSystem.IsWindows() ? "ps1" : "sh";
        var installer = Path.Combine(Path.GetTempPath(), "intellect-yc-" + Guid.NewGuid().ToString("N") + "." + extension);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            await File.WriteAllBytesAsync(installer, await http.GetByteArrayAsync("https://storage.yandexcloud.net/yandexcloud-yc/install." + extension, ct), ct);
            await ProcessRunner.RunAsync(OperatingSystem.IsWindows() ? "powershell" : "bash", OperatingSystem.IsWindows() ? ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", installer] : [installer], ct, interactive: true);
        }
        finally { File.Delete(installer); }
        return File.Exists(usual) ? usual : throw new CommandException("yc не найден после установки. Добавьте его в PATH.");
    }
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        LocalCommands.CheckRoot();
        var action = args.FirstOrDefault() is { } first && !first.StartsWith("--", StringComparison.Ordinal) ? first : "up";
        if (action is not ("up" or "status" or "logs" or "stop" or "start" or "access" or "destroy"))
            throw new CommandException("cloud: неизвестное действие.");
        Dictionary<string, string> options = [];
        for (var i = action == args.FirstOrDefault() ? 1 : 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new CommandException("cloud: ожидается --параметр значение.");
            var key = args[i][2..];
            if (!new[] { "folder-id", "ssh-cidr", "zone", "platform", "cores", "core-fraction", "memory", "disk-size" }.Contains(key))
                throw new CommandException("cloud: неизвестный параметр.");
            options[key] = args[i + 1];
        }
        if (action != "up" && !File.Exists(".deploy/state.json"))
            throw new CommandException("Нет состояния деплоя. Сначала выполните cloud up.");
        var settings = action == "up" ? LocalCommands.Prepare(true) : null;
        var yc = await FindYc(ct);
        try
        {
            await ProcessRunner.RunAsync(yc, ["iam", "create-token"], ct);
        }
        catch (CommandException) { await ProcessRunner.RunAsync(yc, ["init"], ct, timeoutSeconds: 1800, interactive: true); }
        var folder = options.GetValueOrDefault("folder-id") ?? (await ProcessRunner.RunAsync(yc, ["config", "get", "folder-id"], ct)).Trim();
        if (string.IsNullOrWhiteSpace(folder))
            throw new CommandException("Выполните yc init и выберите каталог.");
        var deployment = new CloudCommands(yc, folder, ct);
        try
        {
            if (action == "destroy")
                await deployment.Destroy();
            else if (action == "access")
                await deployment.Access(options.GetValueOrDefault("ssh-cidr") ?? "");
            else if (action == "up")
            {
                Console.WriteLine("Локальный бот с этим же токеном должен быть остановлен.");
                var vm = await deployment.Provision(options);
                if (vm["status"]?.GetValue<string>() == "STOPPED")
                    await deployment.Yc("compute", "instance", "start", "--id", vm["id"]!.GetValue<string>());
                await deployment.Deploy(await deployment.Instance(), settings!);
            }
            else
            {
                var vm = await deployment.Instance();
                if (action is "start" or "stop")
                {
                    var desired = action == "start" ? "RUNNING" : "STOPPED";
                    if (vm["status"]?.GetValue<string>() != desired)
                        await deployment.Yc("compute", "instance", action, "--id", vm["id"]!.GetValue<string>());
                    if (action == "start")
                    {
                        vm = await deployment.Instance();
                        await deployment.WaitSsh(vm);
                        await deployment.Ssh(vm, Compose + " up -d --wait --wait-timeout 180", 240);
                    }
                    Console.WriteLine(action == "start" ? "ВМ запущена." : "ВМ остановлена. Диск продолжает тарифицироваться.");
                }
                else
                {
                    Console.WriteLine($"ВМ: {vm["id"]}; состояние: {vm["status"]}");
                    if (vm["status"]?.GetValue<string>() == "RUNNING")
                    {
                        var output = await deployment.Ssh(vm, Compose + (action == "status" ? " ps" : " logs --tail 100 bot db"));
                        // Only print logs with the matching secret configuration available for masking.
                        Console.WriteLine(new SafeLog(AppSettings.Load(".env.cloud")).Redact(output));
                    }
                }
            }
            return 0;
        }
        catch { Console.Error.WriteLine("Операция не завершена. Состояние сохранено в .deploy/state.json; ресурсы могут тарифицироваться. Повторите команду либо выполните cloud destroy."); throw; }
    }
}
