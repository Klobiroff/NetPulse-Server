using Microsoft.Extensions.DependencyInjection;
using NetPulse.Common.Security;
using NetPulse.Server.Configuration;
using NetPulse.Server.Hosting;
using NetPulse.Server.Networking;
using NetPulse.Server.Security;

namespace NetPulse.Server.Tests;

public sealed class EncryptionConfigTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netpulse-config-" + Guid.NewGuid().ToString("N"));

    public EncryptionConfigTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }

    private static string NoEnvironment(string name) => string.Empty;

    private string WriteKey(string name, byte[]? key = null)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, SecurityKeyText.ToText(key ?? X25519.GeneratePrivateKey()));
        return path;
    }

    private static NetPulseOptions Options(EncryptionMode mode, Action<EncryptionOptions>? configure = null)
    {
        var options = new NetPulseOptions { Encryption = new EncryptionOptions { Mode = mode } };
        configure?.Invoke(options.Encryption);
        return options;
    }

    [Fact]
    public void DisabledModeInstallsNothingEvenWithBrokenEncryptionSettings()
    {
        var options = Options(EncryptionMode.Disabled, e => e.PrivateKeyFile = "does-not-exist");
        Assert.Null(EncryptionSettingsFactory.Create(options, _directory, NoEnvironment));
    }

    [Theory]
    [InlineData(EncryptionMode.Optional)]
    [InlineData(EncryptionMode.Required)]
    public void MissingPrivateKeyFailsClosed(EncryptionMode mode)
    {
        var error = Assert.Throws<InvalidOperationException>(() => EncryptionSettingsFactory.Create(Options(mode), _directory, NoEnvironment));
        Assert.Contains("no private key", error.Message);
    }

    [Fact]
    public void MissingKeyFileFailsClosed()
    {
        var options = Options(EncryptionMode.Required, e => e.PrivateKeyFile = "nope.key");
        var error = Assert.Throws<InvalidOperationException>(() => EncryptionSettingsFactory.Create(options, _directory, NoEnvironment));
        Assert.Contains("does not exist", error.Message);
    }

    [Fact]
    public void MalformedKeyFailsClosedWithoutEchoingTheContent()
    {
        var path = Path.Combine(_directory, "bad.key");
        File.WriteAllText(path, "this-is-a-secret-but-not-a-valid-key");
        var options = Options(EncryptionMode.Required, e => e.PrivateKeyFile = path);
        var error = Assert.Throws<InvalidOperationException>(() => EncryptionSettingsFactory.Create(options, _directory, NoEnvironment));
        Assert.DoesNotContain("this-is-a-secret", error.Message);

        File.WriteAllText(path, Convert.ToBase64String(new byte[31]));
        Assert.Throws<InvalidOperationException>(() => EncryptionSettingsFactory.Create(options, _directory, NoEnvironment));
    }

    [Fact]
    public void KeyCanComeFromTheEnvironmentButNotFromBothSources()
    {
        var key = X25519.GeneratePrivateKey();
        var text = SecurityKeyText.ToText(key);
        string Environment(string name) => name == EncryptionSettingsFactory.PrivateKeyVariable ? text : string.Empty;

        var settings = EncryptionSettingsFactory.Create(Options(EncryptionMode.Required), _directory, Environment);
        Assert.NotNull(settings);
        Assert.Equal(key, settings!.PrivateKeys[0]);
        Assert.False(settings.AllowPlaintext);

        var both = Options(EncryptionMode.Required, e => e.PrivateKeyFile = WriteKey("a.key"));
        var error = Assert.Throws<InvalidOperationException>(() => EncryptionSettingsFactory.Create(both, _directory, Environment));
        Assert.Contains("only one", error.Message);
        Assert.DoesNotContain(text, error.Message);
    }

    [Fact]
    public void OptionalModeAllowsPlaintextAndPreviousKeysAreLoaded()
    {
        var current = WriteKey("current.key");
        var previous = WriteKey("previous.key");
        var options = Options(EncryptionMode.Optional, e =>
        {
            e.PrivateKeyFile = current;
            e.PreviousPrivateKeyFiles = new List<string> { previous };
        });

        var settings = EncryptionSettingsFactory.Create(options, _directory, NoEnvironment)!;
        Assert.True(settings.AllowPlaintext);
        Assert.Equal(2, settings.PrivateKeys.Count);

        options.Encryption.PreviousPrivateKeyFiles = new List<string> { current };
        Assert.Throws<InvalidOperationException>(() => EncryptionSettingsFactory.Create(options, _directory, NoEnvironment));
    }

    [Fact]
    public void RelativeKeyPathsResolveAgainstTheServerFolder()
    {
        WriteKey("relative.key");
        var options = Options(EncryptionMode.Required, e => e.PrivateKeyFile = "relative.key");
        Assert.NotNull(EncryptionSettingsFactory.Create(options, _directory, NoEnvironment));
    }

    [Fact]
    public void PreSharedKeyMustBeValidAndSingleSourced()
    {
        var key = WriteKey("psk-server.key");
        var psk = SecurityKeyText.ToText(X25519.GeneratePrivateKey());
        var good = Options(EncryptionMode.Required, e =>
        {
            e.PrivateKeyFile = key;
            e.PreSharedKey = psk;
        });
        Assert.Equal(32, EncryptionSettingsFactory.Create(good, _directory, NoEnvironment)!.PreSharedKey!.Length);

        good.Encryption.PreSharedKeyFile = WriteKey("psk.key");
        Assert.Throws<InvalidOperationException>(() => EncryptionSettingsFactory.Create(good, _directory, NoEnvironment));

        good.Encryption.PreSharedKeyFile = string.Empty;
        good.Encryption.PreSharedKey = "short";
        Assert.Throws<InvalidOperationException>(() => EncryptionSettingsFactory.Create(good, _directory, NoEnvironment));
    }

    [Theory]
    [InlineData("ReplayWindow", 100)]
    [InlineData("ReplayWindow", 0)]
    [InlineData("CookieLifetimeMs", 10)]
    [InlineData("HandshakeTimeoutMs", 10)]
    [InlineData("PaddingBlock", 5)]
    [InlineData("PaddingBlock", 4096)]
    [InlineData("SessionIdleTimeoutMs", 5)]
    public void OutOfRangeNumbersFailClosed(string name, int value)
    {
        var options = Options(EncryptionMode.Required, e => e.PrivateKeyFile = WriteKey("n.key"));
        typeof(EncryptionOptions).GetProperty(name)!.SetValue(options.Encryption, value);
        Assert.Throws<InvalidOperationException>(() => EncryptionSettingsFactory.Create(options, _directory, NoEnvironment));
    }

    [Fact]
    public void NegativeSecurityLimitsFailClosed()
    {
        var options = Options(EncryptionMode.Disabled);
        options.Security.MaxMessagesPerSecond = -1;
        Assert.Throws<InvalidOperationException>(() => EncryptionSettingsFactory.Create(options, _directory, NoEnvironment));
    }

    [Theory]
    [InlineData("Optional")]
    [InlineData("Required")]
    public async Task ServerRefusesToStartWithoutAUsableKey(string mode)
    {
        var settings = new Dictionary<string, string?>
        {
            ["NetPulse:Port"] = "0",
            ["NetPulse:EnableConsole"] = "false",
            ["NetPulse:Plugins:Directory"] = _directory,
            ["NetPulse:Plugins:HotReload"] = "false",
            ["NetPulse:Encryption:Mode"] = mode
        };

        using var host = ServerHost.Build(Array.Empty<string>(), settings);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());
        Assert.Contains("private key", error.ToString());
    }

    [Fact]
    public void KeygenWritesAFileAndPrintsOnlyThePublicKey()
    {
        var path = Path.Combine(_directory, "generated.key");
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, KeyGenerator.Run(new[] { "--out", path }, output, error));

        Assert.True(SecurityKeyText.TryParseKey(File.ReadAllText(path), out var privateKey));
        var publicText = SecurityKeyText.ToText(X25519.GetPublicKey(privateKey));
        Assert.Contains(publicText, output.ToString());
        Assert.DoesNotContain(SecurityKeyText.ToText(privateKey), output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void KeygenRefusesToOverwriteWithoutForce()
    {
        var path = Path.Combine(_directory, "existing.key");
        File.WriteAllText(path, "keep me");
        var error = new StringWriter();

        Assert.Equal(1, KeyGenerator.Run(new[] { "--out", path }, new StringWriter(), error));
        Assert.Equal("keep me", File.ReadAllText(path));

        Assert.Equal(0, KeyGenerator.Run(new[] { "--out", path, "--force" }, new StringWriter(), error));
        Assert.NotEqual("keep me", File.ReadAllText(path));
        Assert.Equal(2, KeyGenerator.Run(new[] { "--bogus" }, new StringWriter(), error));
    }

    [Fact]
    public void GeneratedKeyWorksWithTheFactory()
    {
        var path = Path.Combine(_directory, "round.key");
        KeyGenerator.Run(new[] { "--out", path }, new StringWriter(), new StringWriter());
        var options = Options(EncryptionMode.Required, e => e.PrivateKeyFile = path);
        Assert.NotNull(EncryptionSettingsFactory.Create(options, _directory, NoEnvironment));
    }
}
