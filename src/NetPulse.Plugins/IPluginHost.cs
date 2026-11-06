using Microsoft.Extensions.Logging;
using NetPulse.Common;

namespace NetPulse.Plugins;

public interface IActor
{
    int ActorId { get; }

    string UserId { get; }

    string NickName { get; }

    PropertyTable Properties { get; }

    bool IsInactive { get; }

    bool IsMasterClient { get; }
}

public interface IPluginHost
{
    string RoomName { get; }

    IReadOnlyList<IActor> Actors { get; }

    int MasterClientId { get; }

    PropertyTable RoomProperties { get; }

    ILogger Logger { get; }

    long ServerTimeMs { get; }

    IActor? GetActor(int actorId);

    INetworkObject? GetNetworkObject(int viewId);

    IReadOnlyCollection<INetworkObject> NetworkObjects { get; }

    void BroadcastEvent(
        byte code,
        object? data,
        ReceiverGroup receivers = ReceiverGroup.All,
        int[]? targets = null,
        CachingOption caching = CachingOption.DoNotCache,
        DeliveryMode delivery = DeliveryMode.ReliableOrdered,
        int senderActorId = 0);

    void SetRoomProperties(PropertyTable props, bool broadcast = true);

    void SetPlayerProperties(int actorId, PropertyTable props, bool broadcast = true);

    void KickActor(int actorId, string reason);

    void CloseRoom();

    IDisposable CreateTimer(TimeSpan dueTime, TimeSpan period, Action callback);

    IDisposable CreateTimer(TimeSpan dueTime, TimeSpan period, Func<CancellationToken, ValueTask> callback);

    void Enqueue(Action action);

    Task<HttpResponseMessage> HttpAsync(HttpRequestMessage request, CancellationToken ct = default);

    void RunInBackground(Func<CancellationToken, Task> work);
}

public interface INetworkObject
{
    int ViewId { get; }

    int CreatorActorId { get; }

    int OwnerActorId { get; }

    ObjectOwnershipMode OwnershipMode { get; }

    bool IsSceneObject { get; }

    bool IsPersistent { get; }
}
