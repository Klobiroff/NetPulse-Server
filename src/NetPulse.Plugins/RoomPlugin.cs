using Microsoft.Extensions.Logging;

namespace NetPulse.Plugins;

public sealed class PluginCreateResult
{
    private PluginCreateResult(IRoomPlugin? plugin, string? error)
    {
        Plugin = plugin;
        Error = error;
    }

    public IRoomPlugin? Plugin { get; }

    public string? Error { get; }

    public static PluginCreateResult Ok(IRoomPlugin plugin) => new(plugin, null);

    public static PluginCreateResult Failed(string error) => new(null, error);
}

public interface IRoomPlugin : IAsyncDisposable
{
    string Name { get; }

    ValueTask OnCreateRoomAsync(ICreateRoomCallInfo info, CancellationToken ct);

    ValueTask OnBeforeJoinAsync(IBeforeJoinCallInfo info, CancellationToken ct);

    ValueTask OnJoinAsync(IJoinCallInfo info, CancellationToken ct);

    ValueTask OnLeaveAsync(ILeaveCallInfo info, CancellationToken ct);

    ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct);

    ValueTask OnBeforeSetPropertiesAsync(IBeforeSetPropertiesCallInfo info, CancellationToken ct);

    ValueTask OnSetPropertiesAsync(ISetPropertiesCallInfo info, CancellationToken ct);

    ValueTask OnBeforeTransferOwnershipAsync(IBeforeTransferOwnershipCallInfo info, CancellationToken ct);

    ValueTask OnBeforeCloseRoomAsync(IBeforeCloseRoomCallInfo info, CancellationToken ct);

    ValueTask OnCloseRoomAsync(ICloseRoomCallInfo info, CancellationToken ct);

    ValueTask OnUnhandledExceptionAsync(Exception ex, ICallInfo? info);
}

public interface IPluginFactory
{
    ValueTask<PluginCreateResult> CreateAsync(IPluginHost host, string pluginName, IReadOnlyDictionary<string, string> config, CancellationToken ct);
}

public abstract class RoomPlugin : IRoomPlugin
{
    protected RoomPlugin(IPluginHost host) => Host = host ?? throw new ArgumentNullException(nameof(host));

    public virtual string Name => GetType().Name;

    protected IPluginHost Host { get; }

    public virtual ValueTask OnCreateRoomAsync(ICreateRoomCallInfo info, CancellationToken ct) => Continue(info);

    public virtual ValueTask OnBeforeJoinAsync(IBeforeJoinCallInfo info, CancellationToken ct) => Continue(info);

    public virtual ValueTask OnJoinAsync(IJoinCallInfo info, CancellationToken ct) => Continue(info);

    public virtual ValueTask OnLeaveAsync(ILeaveCallInfo info, CancellationToken ct) => Continue(info);

    public virtual ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct) => Continue(info);

    public virtual ValueTask OnBeforeSetPropertiesAsync(IBeforeSetPropertiesCallInfo info, CancellationToken ct) => Continue(info);

    public virtual ValueTask OnSetPropertiesAsync(ISetPropertiesCallInfo info, CancellationToken ct) => Continue(info);

    public virtual ValueTask OnBeforeTransferOwnershipAsync(IBeforeTransferOwnershipCallInfo info, CancellationToken ct) => Continue(info);

    public virtual ValueTask OnBeforeCloseRoomAsync(IBeforeCloseRoomCallInfo info, CancellationToken ct) => Continue(info);

    public virtual ValueTask OnCloseRoomAsync(ICloseRoomCallInfo info, CancellationToken ct) => Continue(info);

    public virtual ValueTask OnUnhandledExceptionAsync(Exception ex, ICallInfo? info)
    {
        Host.Logger.LogError(ex, "Unhandled exception in plugin {Plugin}", Name);
        return default;
    }

    public virtual ValueTask DisposeAsync() => default;

    private static ValueTask Continue(ICallInfo info)
    {
        info.Continue();
        return default;
    }
}
