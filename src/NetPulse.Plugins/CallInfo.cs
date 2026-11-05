using NetPulse.Common;

namespace NetPulse.Plugins;

public interface ICallInfo
{
    int ActorId { get; }

    bool IsProcessed { get; }

    void Continue();

    void Fail(string message);
}

public interface ICancellableCallInfo : ICallInfo
{
    void Cancel();
}

public interface ICreateRoomCallInfo : ICallInfo
{
    RoomOptions Options { get; }

    string UserId { get; }

    string NickName { get; }

    PropertyTable PlayerProperties { get; }
}

public interface IBeforeJoinCallInfo : ICallInfo
{
    string UserId { get; }

    string NickName { get; }

    bool IsRejoin { get; }

    PropertyTable PlayerProperties { get; }
}

public interface IJoinCallInfo : ICallInfo
{
    IActor Actor { get; }

    bool IsRejoin { get; }
}

public interface ILeaveCallInfo : ICallInfo
{
    IActor Actor { get; }

    bool IsInactive { get; }

    string Reason { get; }
}

public interface IRaiseEventCallInfo : ICancellableCallInfo
{
    RaiseEventRequest Request { get; }
}

public interface IBeforeSetPropertiesCallInfo : ICancellableCallInfo
{
    int TargetActorId { get; }

    PropertyTable Properties { get; }

    PropertyTable? ExpectedProperties { get; }
}

public interface ISetPropertiesCallInfo : ICallInfo
{
    int TargetActorId { get; }

    PropertyTable Properties { get; }
}

public interface IBeforeCloseRoomCallInfo : ICancellableCallInfo
{
    string Reason { get; }
}

public interface ICloseRoomCallInfo : ICallInfo
{
    string Reason { get; }
}

public interface IBeforeTransferOwnershipCallInfo : ICancellableCallInfo
{
    int ViewId { get; }

    int NewOwnerActorId { get; }

    int CurrentOwnerActorId { get; }

    ObjectOwnershipMode OwnershipMode { get; }

    bool IsRequest { get; }
}
