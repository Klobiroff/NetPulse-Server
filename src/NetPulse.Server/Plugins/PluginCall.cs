using Microsoft.Extensions.Logging;
using NetPulse.Common;
using NetPulse.Plugins;

namespace NetPulse.Server.Plugins;

public enum CallOutcome
{
    Pending,
    Continued,
    Failed,
    Cancelled
}

public sealed class PluginCall :
    ICreateRoomCallInfo,
    IBeforeJoinCallInfo,
    IJoinCallInfo,
    ILeaveCallInfo,
    IRaiseEventCallInfo,
    IBeforeSetPropertiesCallInfo,
    ISetPropertiesCallInfo,
    IBeforeTransferOwnershipCallInfo,
    IBeforeCloseRoomCallInfo,
    ICloseRoomCallInfo
{
    private readonly string _hook;
    private readonly ILogger _logger;
    private readonly bool _cancellable;
    private int _outcome;

    public PluginCall(string hook, ILogger logger, bool cancellable = false)
    {
        _hook = hook;
        _logger = logger;
        _cancellable = cancellable;
    }

    public int ActorId { get; init; }

    public CallOutcome Outcome => (CallOutcome)Volatile.Read(ref _outcome);

    public string? Message { get; private set; }

    public bool IsProcessed => Outcome != CallOutcome.Pending;

    public RoomOptions Options { get; init; } = new();

    public string UserId { get; init; } = string.Empty;

    public string NickName { get; init; } = string.Empty;

    public bool IsRejoin { get; init; }

    public PropertyTable PlayerProperties { get; init; } = new();

    public IActor Actor { get; init; } = null!;

    public bool IsInactive { get; init; }

    public string Reason { get; init; } = string.Empty;

    public RaiseEventRequest Request { get; init; } = null!;

    public int TargetActorId { get; init; }

    public int ViewId { get; init; }

    public int NewOwnerActorId { get; init; }

    public int CurrentOwnerActorId { get; init; }

    public ObjectOwnershipMode OwnershipMode { get; init; }

    public bool IsRequest { get; init; }

    public PropertyTable Properties { get; init; } = new();

    public PropertyTable? ExpectedProperties { get; init; }

    public void Continue() => Complete(CallOutcome.Continued, null);

    public void Fail(string message) => Complete(CallOutcome.Failed, message);

    public void Cancel() => Complete(_cancellable ? CallOutcome.Cancelled : CallOutcome.Failed, "Cancelled");

    public bool FailIfPending(string message) => TrySet(CallOutcome.Failed, message);

    private void Complete(CallOutcome outcome, string? message)
    {
        if (!TrySet(outcome, message))
        {
            _logger.LogWarning("Plugin hook {Hook} was already completed as {Existing}; ignoring {Outcome}", _hook, Outcome, outcome);
        }
    }

    private bool TrySet(CallOutcome outcome, string? message)
    {
        if (Interlocked.CompareExchange(ref _outcome, (int)outcome, (int)CallOutcome.Pending) != (int)CallOutcome.Pending)
        {
            return false;
        }

        Message = message;
        return true;
    }
}
