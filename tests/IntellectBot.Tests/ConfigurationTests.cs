using System.Formats.Tar;
using System.IO.Compression;
using IntellectBot.Configuration;
using IntellectBot.Infrastructure;
using Xunit;

namespace IntellectBot.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("BOT_TOKEN")]
    [InlineData("POSTGRES_PASSWORD")]
    [InlineData("LLM_BASE_URL")]
    [InlineData("LLM_API_KEY")]
    [InlineData("LLM_MODEL")]
    public void MissingRequiredValueIsSafe(string key)
    {
        var values = Fixtures.Values();
        values.Remove(key);
        Assert.Contains(key, Assert.Throws<ConfigException>(() => AppSettings.FromValues(values)).Message);
    }
    [Theory]
    [InlineData("LLM_BASE_URL", "http://example.com")]
    [InlineData("LLM_BASE_URL", "https://secret:password@example.com")]
    [InlineData("LLM_BASE_URL", "https://example.com/?secret=yes")]
    [InlineData("LLM_BASE_URL", "https://exa mple.com")]
    [InlineData("POSTGRES_PORT", "0")]
    [InlineData("HEALTH_PORT", "65536")]
    [InlineData("LLM_CONTEXT_MAX_CHARS", "-1")]
    [InlineData("LLM_REASONING_EFFORT", "extreme")]
    [InlineData("BOT_TOKEN", "bad-token")]
    [InlineData("TELEGRAM_PROXY_URL", "socks5://host")]
    [InlineData("TELEGRAM_PROXY_URL", "https://host:80")]
    [InlineData("TELEGRAM_PROXY_URL", "http://host:80/path")]
    public void InvalidConfigurationIsRejected(string key, string value)
    {
        var values = Fixtures.Values();
        values[key] = value;
        var error = Assert.Throws<ConfigException>(() => AppSettings.FromValues(values));
        Assert.Contains(key, error.Message);
        Assert.DoesNotContain("password@", error.Message);
    }
    [Theory]
    [InlineData("http://host:3128")]
    [InlineData("socks5://user:p%40ss@host:1080")]
    [InlineData("http://host:80")]
    public void SupportedProxyIsAccepted(string proxy)
    {
        var values = Fixtures.Values();
        values["TELEGRAM_PROXY_URL"] = proxy;
        Assert.Equal(proxy, AppSettings.FromValues(values).TelegramProxyUrl);
    }
    [Fact]
    public void DotEnvSupportsQuotesCommentsAndNoInterpolation()
    {
        var env = DotEnv.Parse("export A='a # b $HOME ${USER}'\nB=unquoted # comment\nC=\"a\\nb\"\nD='it\\'s'\n");
        Assert.Equal("a # b $HOME ${USER}", env["A"]);
        Assert.Equal("unquoted", env["B"]);
        Assert.Equal("a\nb", env["C"]);
        Assert.Equal("it's", env["D"]);
    }
    [Theory]
    [InlineData("a'b\\c$HOME#d")]
    [InlineData("  spaces  ")]
    [InlineData("two\nlines")]
    public void DotEnvQuoteRoundTrips(string value) => Assert.Equal(value, DotEnv.Parse("VALUE=" + DotEnv.Quote(value))["VALUE"]);
    [Fact]
    public void ComposeQuotingKeepsBackslashesLiteral()
    {
        Assert.Equal("'a\\b$VALUE\\'c'", DotEnv.ComposeQuote("a\\b$VALUE'c"));
    }
    [Fact]
    public void EnvironmentOverridesFile()
    {
        var key = "INTELLECT_TEST_" + Guid.NewGuid().ToString("N");
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, key + "=file\n");
            Environment.SetEnvironmentVariable(key, "process");
            Assert.Equal("process", DotEnv.Load(path)[key]);
        }
        finally { Environment.SetEnvironmentVariable(key, null); File.Delete(path); }
    }
    [Fact]
    public void PasswordWhitespaceIsPreservedAndConnectionStringEscaped()
    {
        var values = Fixtures.Values();
        values["POSTGRES_PASSWORD"] = " p;ass='word' ";
        var settings = AppSettings.FromValues(values);
        Assert.Equal(values["POSTGRES_PASSWORD"], new Npgsql.NpgsqlConnectionStringBuilder(settings.ConnectionString).Password);
    }
    [Fact]
    public void LogMasksSecretsAndLineInjection()
    {
        var log = Fixtures.Log;
        var text = log.Redact("123456:test-token test-api-key test-password\nhttp://user:pass@proxy");
        Assert.DoesNotContain("test-api-key", text);
        Assert.DoesNotContain("test-password", text);
        Assert.DoesNotContain("test-token", text);
        Assert.DoesNotContain("user:pass", text);
        Assert.DoesNotContain('\n', text);
    }
    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::1/128")]
    [InlineData("192.0.2.5/24")]
    [InlineData("example.com/32")]
    public void UnsafeCloudCidrRejected(string cidr) => Assert.Throws<CommandException>(() => CloudCommands.ValidateCidr(cidr));
    [Theory]
    [InlineData("192.0.2.5/32")]
    [InlineData("192.0.2.0/24")]
    public void ValidCloudCidrAccepted(string cidr) => Assert.Equal(cidr, CloudCommands.ValidateCidr(cidr));
    [Fact]
    public void SourceArchiveExcludesSecretsAndBuildOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "intellect-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "App", "bin"));
        try
        {
            foreach (var file in new[] { "Dockerfile", ".dockerignore", "compose.yaml", "Directory.Build.props", "global.json", ".env", "src/App/Program.cs", "src/App/.env", "src/App/bin/secret.cs" })
                File.WriteAllText(Path.Combine(root, file), "test");
            using var stream = new MemoryStream(CloudCommands.SourceArchive(root));
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            List<string> paths = [];
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
                paths.Add(entry.Name);
            Assert.Contains("src/App/Program.cs", paths);
            Assert.DoesNotContain(paths, p => p.Contains(".env", StringComparison.Ordinal) || p.Contains("/bin/", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }
}
