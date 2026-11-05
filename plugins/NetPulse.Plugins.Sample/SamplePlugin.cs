using Microsoft.Extensions.Logging;
using NetPulse.Common;

namespace NetPulse.Plugins.Sample;

public sealed class SamplePluginFactory : IPluginFactory
{
    public ValueTask<PluginCreateResult> CreateAsync(IPluginHost host, string pluginName, IReadOnlyDictionary<string, string> config, CancellationToken ct)
    {
        var scorePerPoint = ReadInt(config, "ScorePerPoint", 1);
        var joinCheckDelayMs = ReadInt(config, "JoinCheckDelayMs", 10);
        return ValueTask.FromResult(PluginCreateResult.Ok(new SamplePlugin(host, scorePerPoint, joinCheckDelayMs)));
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> config, string key, int fallback) =>
        config.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed) ? parsed : fallback;
}

public sealed class SamplePlugin : RoomPlugin
{
    public const byte AddScoreEvent = 10;
    public const byte TickEvent = 50;
    public const byte ForbiddenEvent = 99;

    private readonly int _scorePerPoint;
    private readonly int _joinCheckDelayMs;
    private IDisposable? _timer;
    private int _tick;

    public SamplePlugin(IPluginHost host, int scorePerPoint, int joinCheckDelayMs)
        : base(host)
    {
        _scorePerPoint = scorePerPoint;
        _joinCheckDelayMs = joinCheckDelayMs;
    }

    public override string Name => "Sample";

    public override async ValueTask OnBeforeJoinAsync(IBeforeJoinCallInfo info, CancellationToken ct)
    {
        await Task.Delay(_joinCheckDelayMs, ct);

        if (info.NickName.Equals("banned", StringComparison.OrdinalIgnoreCase))
        {
            info.Fail("This nickname is banned");
            return;
        }

        info.Continue();
    }

    public override ValueTask OnJoinAsync(IJoinCallInfo info, CancellationToken ct)
    {
        _timer ??= Host.CreateTimer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), OnTickAsync);

        if (!info.IsRejoin)
        {
            Host.SetRoomProperties(new PropertyTable { [ScoreKey(info.ActorId)] = 0 });
        }

        info.Continue();
        return default;
    }

    public override ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
    {
        switch (info.Request.EventCode)
        {
            case ForbiddenEvent:
                info.Fail("Event 99 is not allowed in this room");
                break;
            case AddScoreEvent:
                AddScore(info);
                break;
            default:
                info.Continue();
                break;
        }

        return default;
    }

    public override ValueTask OnCloseRoomAsync(ICloseRoomCallInfo info, CancellationToken ct)
    {
        _timer?.Dispose();
        _timer = null;
        info.Continue();
        return default;
    }

    private static string ScoreKey(int actorId) => $"score_{actorId}";

    private void AddScore(IRaiseEventCallInfo info)
    {
        var points = info.Request.Data is int or byte or short ? Convert.ToInt32(info.Request.Data) : 0;

        if (points is < 1 or > 100)
        {
            info.Fail("Score points must be between 1 and 100");
            return;
        }

        var key = ScoreKey(info.ActorId);
        var score = Host.RoomProperties.Get<int>(key) + (points * _scorePerPoint);
        Host.SetRoomProperties(new PropertyTable { [key] = score });
        Host.Logger.LogDebug("Actor {Actor} scored, total {Score}", info.ActorId, score);
        info.Cancel();
    }

    private async ValueTask OnTickAsync(CancellationToken ct)
    {
        await Task.Yield();
        Host.BroadcastEvent(TickEvent, ++_tick, caching: CachingOption.DoNotCache);
    }
}
