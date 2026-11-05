namespace NetPulse.Server.Configuration;

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
