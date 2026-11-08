using NetPulse.Common.Security;
using NetPulse.Server.Configuration;

namespace NetPulse.Server.Security;

public static class EncryptionSettingsFactory
{
    public const string PrivateKeyVariable = "NETPULSE_SERVER_PRIVATE_KEY";

    public static SecureServerSettings? Create(NetPulseOptions options, string baseDirectory, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        SecurityOptionsValidator.Validate(options.Security);
        var encryption = options.Encryption;

        if (!Enum.IsDefined(encryption.Mode))
        {
            throw new InvalidOperationException("NetPulse:Encryption:Mode is not a valid mode (Disabled, Optional or Required)");
        }

        if (encryption.Mode == EncryptionMode.Disabled)
        {
            return null;
        }

        ValidateNumbers(encryption);
        var keys = new List<byte[]> { LoadCurrentKey(encryption, baseDirectory, environment) };

        foreach (var file in encryption.PreviousPrivateKeyFiles)
        {
            var previous = ReadKeyFile(file, baseDirectory, "NetPulse:Encryption:PreviousPrivateKeyFiles");

            if (keys.Any(existing => existing.AsSpan().SequenceEqual(previous)))
            {
                throw new InvalidOperationException("NetPulse:Encryption:PreviousPrivateKeyFiles contains a key that is already configured");
            }

            keys.Add(previous);
        }

        return new SecureServerSettings
        {
            AllowPlaintext = encryption.Mode == EncryptionMode.Optional,
            PrivateKeys = keys,
            PreSharedKey = LoadPreSharedKey(encryption, baseDirectory),
            RequireCookie = encryption.RequireCookie,
            CookieLifetimeMs = encryption.CookieLifetimeMs,
            SessionIdleTimeoutMs = encryption.SessionIdleTimeoutMs,
            ReplaceIdleMs = encryption.ReplaceIdleMs,
            HandshakeTimeoutMs = encryption.HandshakeTimeoutMs,
            HandshakesPerIpPerSecond = options.Security.HandshakesPerIpPerSecond,
            MaxHandshakesPerSecond = options.Security.MaxHandshakesPerSecond,
            MaxSessions = Math.Max(1024, options.MaxConnections * 4),
            Session = new SecureSessionSettings
            {
                RekeyAfterPackets = encryption.RekeyAfterPackets,
                RekeyAfterMs = encryption.RekeyAfterMs,
                ReplayWindow = encryption.ReplayWindow,
                PaddingBlock = encryption.PaddingBlock
            }
        };
    }

    public static string ResolvePath(string path, string baseDirectory) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDirectory, path));

    private static void ValidateNumbers(EncryptionOptions encryption)
    {
        Require(encryption.CookieLifetimeMs >= 1000, "CookieLifetimeMs must be at least 1000");
        Require(encryption.RekeyAfterPackets >= 0, "RekeyAfterPackets must not be negative");
        Require(encryption.RekeyAfterMs >= 0, "RekeyAfterMs must not be negative");
        Require(encryption.ReplayWindow >= 64 && encryption.ReplayWindow <= 65536 && encryption.ReplayWindow % 64 == 0, "ReplayWindow must be a multiple of 64 between 64 and 65536");
        Require(encryption.SessionIdleTimeoutMs >= 1000, "SessionIdleTimeoutMs must be at least 1000");
        Require(encryption.ReplaceIdleMs >= 0, "ReplaceIdleMs must not be negative");
        Require(encryption.HandshakeTimeoutMs >= 500, "HandshakeTimeoutMs must be at least 500");
        Require(encryption.PaddingBlock == 0 || (encryption.PaddingBlock >= 16 && encryption.PaddingBlock <= SecureRecord.MaxPaddingBlock), "PaddingBlock must be 0 or between 16 and 512");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"NetPulse:Encryption:{message}");
        }
    }

    private static byte[] LoadCurrentKey(EncryptionOptions encryption, string baseDirectory, Func<string, string?> environment)
    {
        var fromEnvironment = environment(PrivateKeyVariable);
        var hasFile = !string.IsNullOrWhiteSpace(encryption.PrivateKeyFile);
        var hasVariable = !string.IsNullOrWhiteSpace(fromEnvironment);

        if (hasFile && hasVariable)
        {
            throw new InvalidOperationException($"Both NetPulse:Encryption:PrivateKeyFile and {PrivateKeyVariable} are set; configure only one");
        }

        if (!hasFile && !hasVariable)
        {
            throw new InvalidOperationException($"NetPulse:Encryption:Mode is {encryption.Mode} but no private key is configured; run 'NetPulse.Server keygen' and set NetPulse:Encryption:PrivateKeyFile or {PrivateKeyVariable}");
        }

        if (hasFile)
        {
            return ReadKeyFile(encryption.PrivateKeyFile, baseDirectory, "NetPulse:Encryption:PrivateKeyFile");
        }

        return ParseKey(fromEnvironment!, PrivateKeyVariable);
    }

    private static byte[] ReadKeyFile(string path, string baseDirectory, string setting)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{setting} contains an empty path");
        }

        var resolved = ResolvePath(path, baseDirectory);

        if (!File.Exists(resolved))
        {
            throw new InvalidOperationException($"{setting}: key file '{resolved}' does not exist");
        }

        return ParseKey(File.ReadAllText(resolved), $"{setting} ('{resolved}')");
    }

    private static byte[] ParseKey(string text, string source)
    {
        if (!SecurityKeyText.TryParseKey(text, out var key))
        {
            throw new InvalidOperationException($"{source}: the value is not a Base64-encoded 32-byte key");
        }

        return key;
    }

    private static byte[]? LoadPreSharedKey(EncryptionOptions encryption, string baseDirectory)
    {
        var hasValue = !string.IsNullOrWhiteSpace(encryption.PreSharedKey);
        var hasFile = !string.IsNullOrWhiteSpace(encryption.PreSharedKeyFile);

        if (hasValue && hasFile)
        {
            throw new InvalidOperationException("Both NetPulse:Encryption:PreSharedKey and PreSharedKeyFile are set; configure only one");
        }

        if (hasValue)
        {
            return ParseKey(encryption.PreSharedKey, "NetPulse:Encryption:PreSharedKey");
        }

        return hasFile ? ReadKeyFile(encryption.PreSharedKeyFile, baseDirectory, "NetPulse:Encryption:PreSharedKeyFile") : null;
    }
}

public static class SecurityOptionsValidator
{
    public static void Validate(SecurityOptions security)
    {
        Require(security.MaxConnectionsPerIp, nameof(security.MaxConnectionsPerIp));
        Require(security.HandshakesPerIpPerSecond, nameof(security.HandshakesPerIpPerSecond));
        Require(security.MaxHandshakesPerSecond, nameof(security.MaxHandshakesPerSecond));
        Require(security.ConnectionAttemptsPerIpPerSecond, nameof(security.ConnectionAttemptsPerIpPerSecond));
        Require(security.AuthDeadlineMs, nameof(security.AuthDeadlineMs));
        Require(security.MaxAuthAttempts, nameof(security.MaxAuthAttempts));
        Require(security.MaxMessagesPerSecond, nameof(security.MaxMessagesPerSecond));
        Require(security.MessageBurst, nameof(security.MessageBurst));
        Require(security.MaxBytesPerSecond, nameof(security.MaxBytesPerSecond));
        Require(security.ByteBurst, nameof(security.ByteBurst));
        Require(security.MaxMessageBytes, nameof(security.MaxMessageBytes));
        Require(security.MaxUserIdLength, nameof(security.MaxUserIdLength));
        Require(security.MaxNickNameLength, nameof(security.MaxNickNameLength));
        Require(security.MaxAuthTokenLength, nameof(security.MaxAuthTokenLength));
        Require(security.MaxRoomNameLength, nameof(security.MaxRoomNameLength));
        Require(security.MaxPropertiesPerRoom, nameof(security.MaxPropertiesPerRoom));
        Require(security.MaxPropertyBytes, nameof(security.MaxPropertyBytes));
        Require(security.MaxCachedEventsPerRoom, nameof(security.MaxCachedEventsPerRoom));
        Require(security.MaxTargetActors, nameof(security.MaxTargetActors));
    }

    private static void Require(int value, string name)
    {
        if (value < 0)
        {
            throw new InvalidOperationException($"NetPulse:Security:{name} must not be negative (0 disables the limit)");
        }
    }
}
