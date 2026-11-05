using Microsoft.Extensions.Logging;
using NetPulse.Server.Networking;

namespace NetPulse.Server.Core;

public sealed class FloodGuard
{
    private readonly ILogger _logger;
    private readonly ServerMetrics _metrics;
    private readonly int _peerLimit;

    public FloodGuard(ILogger logger, ServerMetrics metrics, int peerLimit)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _peerLimit = Math.Max(1, peerLimit);
    }

    public PostResult TryPost(Fiber fiber, Peer peer, Func<ValueTask> work, string scope)
    {
        if (!peer.TryAcquireSlot(_peerLimit))
        {
            Overload(peer, scope, $"exceeded its limit of {_peerLimit} queued messages");
            return PostResult.Full;
        }

        var result = fiber.TryPost(work, peer, peer.ReleaseSlot, out var heaviest);

        if (result == PostResult.Accepted)
        {
            return result;
        }

        peer.ReleaseSlot();

        if (result == PostResult.Full)
        {
            _logger.LogWarning("Queue of {Scope} is full; dropping a message from peer {Peer}", scope, peer.Id);

            if (heaviest is Peer victim)
            {
                Overload(victim, scope, "has the most queued messages while the queue is full");
            }
        }

        return result;
    }

    private void Overload(Peer peer, string scope, string reason)
    {
        if (!peer.MarkOverloaded())
        {
            return;
        }

        _metrics.PeerOverloaded();
        _logger.LogWarning("Disconnecting peer {Peer} ({Scope}): {Reason}", peer.Id, scope, reason);
        peer.Disconnect("Overloaded");
    }
}
