using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetPulse.Server.Configuration;
using NetPulse.Server.Lobby;
using NetPulse.Server.Networking;
using NetPulse.Server.Plugins;
using SystemConsole = System.Console;

namespace NetPulse.Server.Commands;

public sealed class StatsReporter
{
    private readonly Core.ServerMetrics _metrics;
    private readonly Core.ServerClock _clock;
    private readonly LobbyService _lobby;
    private readonly PluginManager _plugins;

    public StatsReporter(Core.ServerMetrics metrics, Core.ServerClock clock, LobbyService lobby, PluginManager plugins)
    {
        _metrics = metrics;
        _clock = clock;
        _lobby = lobby;
        _plugins = plugins;
    }

    public async Task<string> BuildAsync()
    {
        var uptime = TimeSpan.FromMilliseconds(_clock.NowMs);
        var lines = new List<string>
        {
            $"uptime      {uptime:d\\.hh\\:mm\\:ss}",
            $"peers       {_metrics.Peers}",
            $"rooms       {_metrics.Rooms}",
            $"msgs in/out {_metrics.MessagesIn} / {_metrics.MessagesOut}",
            $"bytes in/out {_metrics.BytesIn} / {_metrics.BytesOut}",
            $"plugin errs {_metrics.PluginErrors}"
        };

        foreach (var plugin in _plugins.Plugins)
        {
            lines.Add($"plugin      {plugin.Name} v{plugin.Version} (rooms: {plugin.ActiveLeases})");
        }

        foreach (var room in await _lobby.SnapshotRoomsAsync())
        {
            lines.Add($"room        {room.Name} {room.PlayerCount}/{room.MaxPlayers} open={room.IsOpen}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class ConsoleCommandService : BackgroundService
{
    private readonly NetPulseOptions _options;
    private readonly StatsReporter _stats;
    private readonly PluginManager _plugins;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ConsoleCommandService> _logger;

    public ConsoleCommandService(
        IOptions<NetPulseOptions> options,
        StatsReporter stats,
        PluginManager plugins,
        IHostApplicationLifetime lifetime,
        ILogger<ConsoleCommandService> logger)
    {
        _options = options.Value;
        _stats = stats;
        _plugins = plugins;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableConsole)
        {
            return Task.CompletedTask;
        }

        return ReadLoopAsync(stoppingToken);
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        _logger.LogInformation("Console commands: stats, plugins, reload <folder>, quit");

        while (!token.IsCancellationRequested)
        {
            string? line;

            try
            {
                line = await SystemConsole.In.ReadLineAsync(token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            await ExecuteAsync(line.Trim());
        }
    }

    private async Task ExecuteAsync(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "stats":
                SystemConsole.WriteLine(await _stats.BuildAsync());
                break;
            case "plugins":
                foreach (var plugin in _plugins.Plugins)
                {
                    SystemConsole.WriteLine($"{plugin.Name} v{plugin.Version} leases={plugin.ActiveLeases}");
                }

                break;
            case "reload" when parts.Length == 2:
                SystemConsole.WriteLine(_plugins.Reload(parts[1]) ? "reloaded" : "reload failed");
                break;
            case "quit":
            case "exit":
                _lifetime.StopApplication();
                break;
            default:
                SystemConsole.WriteLine("Unknown command. Available: stats, plugins, reload <folder>, quit");
                break;
        }
    }
}
