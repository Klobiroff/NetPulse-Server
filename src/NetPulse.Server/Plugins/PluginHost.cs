using Microsoft.Extensions.Logging;
using NetPulse.Common;
using NetPulse.Plugins;
using NetPulse.Server.Core;
using NetPulse.Server.Rooms;

namespace NetPulse.Server.Plugins;

public sealed class PluginHost : IPluginHost
{
    public const string HttpClientName = "netpulse-plugins";

    private readonly Room _room;
    private readonly ServerClock _clock;
    private readonly IHttpClientFactory _httpFactory;
    private readonly HashSet<AsyncPluginTimer> _timers = new();
    private readonly CancellationTokenSource _lifetime = new();

    public PluginHost(Room room, ILogger logger, ServerClock clock, IHttpClientFactory httpFactory)
    {
        _room = room;
        Logger = logger;
        _clock = clock;
        _httpFactory = httpFactory;
    }

    public string RoomName => _room.Name;

    public IReadOnlyList<IActor> Actors => _room.ActorSnapshot();

    public int MasterClientId => _room.MasterClientId;

    public PropertyTable RoomProperties => _room.Properties;

    public ILogger Logger { get; }

    public long ServerTimeMs => _clock.NowMs;

    public IActor? GetActor(int actorId) => _room.FindActor(actorId);

    public INetworkObject? GetNetworkObject(int viewId) => _room.FindObject(viewId);

    public IReadOnlyCollection<INetworkObject> NetworkObjects => _room.ObjectSnapshot();

    public void BroadcastEvent(
        byte code,
        object? data,
        ReceiverGroup receivers = ReceiverGroup.All,
        int[]? targets = null,
        CachingOption caching = CachingOption.DoNotCache,
        DeliveryMode delivery = DeliveryMode.ReliableOrdered,
        int senderActorId = 0)
    {
        var request = new RaiseEventRequest
        {
            EventCode = code,
            Data = data,
            Receivers = receivers,
            TargetActors = targets,
            Caching = caching,
            Delivery = delivery
        };

        _room.RunOnFiber(() => _room.Deliver(request, senderActorId));
    }

    public void SetRoomProperties(PropertyTable props, bool broadcast = true) =>
        _room.RunOnFiber(() => _room.ApplyPropertiesFromPlugin(0, props, broadcast));

    public void SetPlayerProperties(int actorId, PropertyTable props, bool broadcast = true) =>
        _room.RunOnFiber(() => _room.ApplyPropertiesFromPlugin(actorId, props, broadcast));

    public void KickActor(int actorId, string reason) => _room.RunOnFiber(() => _room.PostKick(actorId, reason));

    public void CloseRoom() => _room.RunOnFiber(() => _room.PostClose());

    public IDisposable CreateTimer(TimeSpan dueTime, TimeSpan period, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        return CreateTimer(dueTime, period, _ =>
        {
            callback();
            return ValueTask.CompletedTask;
        });
    }

    public IDisposable CreateTimer(TimeSpan dueTime, TimeSpan period, Func<CancellationToken, ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new AsyncPluginTimer(this, callback, dueTime, period);
        _room.RunOnFiber(() =>
        {
            if (_lifetime.IsCancellationRequested)
            {
                timer.Dispose();
                return;
            }

            _timers.Add(timer);
            _ = timer.RunAsync();
        });
        return timer;
    }

    public void Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _room.PostGuarded(action);
    }

    public Task<HttpResponseMessage> HttpAsync(HttpRequestMessage request, CancellationToken ct = default) =>
        _httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);

    public void RunInBackground(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var token = _lifetime.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await work(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Background work in room {Room} failed", RoomName);
            }
        });
    }

    public void Shutdown()
    {
        _lifetime.Cancel();

        foreach (var timer in _timers.ToArray())
        {
            timer.Dispose();
        }

        _timers.Clear();
    }

    private sealed class AsyncPluginTimer : IDisposable
    {
        private readonly PluginHost _owner;
        private readonly Func<CancellationToken, ValueTask> _callback;
        private readonly TimeSpan _due;
        private readonly TimeSpan _period;
        private readonly CancellationTokenSource _cts = new();

        public AsyncPluginTimer(PluginHost owner, Func<CancellationToken, ValueTask> callback, TimeSpan due, TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _due = due;
            _period = period;
        }

        public async Task RunAsync()
        {
            var token = _cts.Token;

            try
            {
                if (_due > TimeSpan.Zero)
                {
                    await Task.Delay(_due, token);
                }

                if (_period <= TimeSpan.Zero || _period == Timeout.InfiniteTimeSpan)
                {
                    await InvokeAsync(token);
                    return;
                }

                using var periodic = new PeriodicTimer(_period);

                do
                {
                    await InvokeAsync(token);
                }
                while (await periodic.WaitForNextTickAsync(token));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _owner._timers.Remove(this);
            }
        }

        public void Dispose() => _cts.Cancel();

        private async ValueTask InvokeAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _callback(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await _owner._room.ReportExceptionAsync(ex);
            }
        }
    }
}
