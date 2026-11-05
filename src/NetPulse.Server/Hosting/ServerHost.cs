using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetPulse.Server.Configuration;
using NetPulse.Server.Commands;
using NetPulse.Server.Core;
using NetPulse.Server.Lobby;
using NetPulse.Server.Networking;
using NetPulse.Server.Plugins;
using NetPulse.Server.Rooms;

namespace NetPulse.Server.Hosting;

public static class ServerHost
{
    public static IHost Build(string[] args, IEnumerable<KeyValuePair<string, string?>>? overrides = null, Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder(args);

        if (overrides is not null)
        {
            builder.Configuration.AddInMemoryCollection(overrides);
        }

        builder.Services.Configure<NetPulseOptions>(builder.Configuration.GetSection(NetPulseOptions.SectionName));
        builder.Services.AddHttpClient(PluginHost.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
        builder.Services.AddSingleton<ServerClock>();
        builder.Services.AddSingleton<ServerMetrics>();
        builder.Services.AddSingleton<PluginManager>();
        builder.Services.AddSingleton<RoomServices>();
        builder.Services.AddSingleton<LobbyService>();
        builder.Services.AddSingleton<Authenticator>();
        builder.Services.AddSingleton<NetPulseServer>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<NetPulseServer>());
        builder.Services.AddSingleton<StatsReporter>();
        builder.Services.AddHostedService<ConsoleCommandService>();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }
}
