namespace NetPulse.Server.Configuration;

public enum EncryptionMode
{
    Disabled,
    Optional,
    Required
}

public sealed class NetPulseOptions
{
    public const string SectionName = "NetPulse";

    public int Port { get; set; } = 5055;

    public string ConnectionKey { get; set; } = "NetPulse";

    public int MaxConnections { get; set; } = 2000;

    public string AppId { get; set; } = string.Empty;

    public bool AppVersionCheck { get; set; } = true;

    public int TickRate { get; set; } = 30;

    public int DisconnectTimeoutMs { get; set; } = 10000;

    public bool EnableConsole { get; set; } = true;

    public int RoomListIntervalMs { get; set; } = 1000;

    public int RoomQueueCapacity { get; set; } = 4096;

    public int PeerQueueLimit { get; set; } = 256;

    public int OwnershipRequestTimeoutMs { get; set; } = 5000;

    public int ShutdownTimeoutMs { get; set; } = 10000;

    public PluginsOptions Plugins { get; set; } = new();

    public EncryptionOptions Encryption { get; set; } = new();

    public SecurityOptions Security { get; set; } = new();
}

public sealed class EncryptionOptions
{
    public EncryptionMode Mode { get; set; } = EncryptionMode.Disabled;

    public string PrivateKeyFile { get; set; } = string.Empty;

    public List<string> PreviousPrivateKeyFiles { get; set; } = new();

    public string PreSharedKey { get; set; } = string.Empty;

    public string PreSharedKeyFile { get; set; } = string.Empty;

    public bool RequireCookie { get; set; } = true;

    public int CookieLifetimeMs { get; set; } = 30000;

    public long RekeyAfterPackets { get; set; } = 1000000;

    public int RekeyAfterMs { get; set; } = 600000;

    public int ReplayWindow { get; set; } = 4096;

    public int SessionIdleTimeoutMs { get; set; } = 30000;

    public int ReplaceIdleMs { get; set; } = 3000;

    public int HandshakeTimeoutMs { get; set; } = 5000;

    public int PaddingBlock { get; set; }
}

public sealed class SecurityOptions
{
    public int MaxConnectionsPerIp { get; set; } = 32;

    public int HandshakesPerIpPerSecond { get; set; } = 10;

    public int MaxHandshakesPerSecond { get; set; } = 2000;

    public int ConnectionAttemptsPerIpPerSecond { get; set; } = 20;

    public int AuthDeadlineMs { get; set; } = 10000;

    public int MaxAuthAttempts { get; set; } = 5;

    public int MaxMessagesPerSecond { get; set; } = 1000;

    public int MessageBurst { get; set; } = 2000;

    public int MaxBytesPerSecond { get; set; } = 1048576;

    public int ByteBurst { get; set; } = 2097152;

    public int MaxMessageBytes { get; set; } = 262144;

    public int MaxUserIdLength { get; set; } = 128;

    public int MaxNickNameLength { get; set; } = 64;

    public int MaxAuthTokenLength { get; set; } = 8192;

    public int MaxRoomNameLength { get; set; } = 128;

    public int MaxPropertiesPerRoom { get; set; } = 256;

    public int MaxPropertyBytes { get; set; } = 16384;

    public int MaxCachedEventsPerRoom { get; set; } = 4096;

    public int MaxTargetActors { get; set; } = 256;

    public bool RequireRejoinToken { get; set; }
}

public sealed class PluginsOptions
{
    public string Directory { get; set; } = "plugins";

    public string Default { get; set; } = string.Empty;

    public string Auth { get; set; } = string.Empty;

    public bool HotReload { get; set; } = true;

    public int HookTimeoutMs { get; set; } = 5000;

    public int AuthTimeoutMs { get; set; } = 5000;

    public Dictionary<string, Dictionary<string, string>> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
