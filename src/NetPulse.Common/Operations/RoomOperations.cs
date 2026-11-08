namespace NetPulse.Common
{
    public sealed class CreateRoomRequest
    {
        public string RoomName { get; set; }

        public RoomOptions Options { get; set; }

        public PropertyTable PlayerProperties { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.RoomName] = RoomName,
            [ParamKey.Options] = Options?.ToParameters(),
            [ParamKey.PlayerProperties] = PlayerProperties
        };

        public static CreateRoomRequest FromParameters(ParameterDictionary parameters) => new()
        {
            RoomName = parameters.Get<string>(ParamKey.RoomName),
            Options = RoomOptions.FromParameters(parameters.Get<ParameterDictionary>(ParamKey.Options)),
            PlayerProperties = parameters.Get<PropertyTable>(ParamKey.PlayerProperties)
        };
    }

    public sealed class JoinOrCreateRoomRequest
    {
        public string RoomName { get; set; }

        public RoomOptions Options { get; set; }

        public PropertyTable PlayerProperties { get; set; }

        public string RejoinToken { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.RoomName] = RoomName,
            [ParamKey.Options] = Options?.ToParameters(),
            [ParamKey.PlayerProperties] = PlayerProperties,
            [ParamKey.RejoinToken] = RejoinToken
        };

        public static JoinOrCreateRoomRequest FromParameters(ParameterDictionary parameters) => new()
        {
            RoomName = parameters.Get<string>(ParamKey.RoomName),
            Options = RoomOptions.FromParameters(parameters.Get<ParameterDictionary>(ParamKey.Options)),
            PlayerProperties = parameters.Get<PropertyTable>(ParamKey.PlayerProperties),
            RejoinToken = parameters.Get<string>(ParamKey.RejoinToken)
        };
    }

    public sealed class JoinRoomRequest
    {
        public string RoomName { get; set; }

        public PropertyTable PlayerProperties { get; set; }

        public string RejoinToken { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.RoomName] = RoomName,
            [ParamKey.PlayerProperties] = PlayerProperties,
            [ParamKey.RejoinToken] = RejoinToken
        };

        public static JoinRoomRequest FromParameters(ParameterDictionary parameters) => new()
        {
            RoomName = parameters.Get<string>(ParamKey.RoomName),
            PlayerProperties = parameters.Get<PropertyTable>(ParamKey.PlayerProperties),
            RejoinToken = parameters.Get<string>(ParamKey.RejoinToken)
        };
    }

    public sealed class JoinRandomRequest
    {
        public PropertyTable ExpectedProperties { get; set; }

        public byte ExpectedMaxPlayers { get; set; }

        public PropertyTable PlayerProperties { get; set; }

        public RoomOptions CreateOptions { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.ExpectedProperties] = ExpectedProperties,
            [ParamKey.ExpectedMaxPlayers] = ExpectedMaxPlayers,
            [ParamKey.PlayerProperties] = PlayerProperties,
            [ParamKey.Options] = CreateOptions?.ToParameters()
        };

        public static JoinRandomRequest FromParameters(ParameterDictionary parameters) => new()
        {
            ExpectedProperties = parameters.Get<PropertyTable>(ParamKey.ExpectedProperties),
            ExpectedMaxPlayers = parameters.Get<byte>(ParamKey.ExpectedMaxPlayers),
            PlayerProperties = parameters.Get<PropertyTable>(ParamKey.PlayerProperties),
            CreateOptions = RoomOptions.FromParameters(parameters.Get<ParameterDictionary>(ParamKey.Options))
        };
    }

    public sealed class JoinRoomResponse
    {
        public string RoomName { get; set; }

        public int LocalActorId { get; set; }

        public int MasterClientId { get; set; }

        public PlayerInfo[] Players { get; set; } = System.Array.Empty<PlayerInfo>();

        public PropertyTable RoomProperties { get; set; }

        public long ServerTimeTicks { get; set; }

        public byte MaxPlayers { get; set; }

        public bool IsOpen { get; set; } = true;

        public bool IsVisible { get; set; } = true;

        public bool IsRejoin { get; set; }

        public int TickRate { get; set; }

        public string RejoinToken { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.RoomName] = RoomName,
            [ParamKey.ActorId] = LocalActorId,
            [ParamKey.MasterClientId] = MasterClientId,
            [ParamKey.Players] = PlayerInfo.ToObjectArray(Players),
            [ParamKey.RoomProperties] = RoomProperties,
            [ParamKey.ServerTimeTicks] = ServerTimeTicks,
            [ParamKey.MaxPlayers] = MaxPlayers,
            [ParamKey.IsOpen] = IsOpen,
            [ParamKey.IsVisible] = IsVisible,
            [ParamKey.IsRejoin] = IsRejoin,
            [ParamKey.TickRate] = TickRate,
            [ParamKey.RejoinToken] = RejoinToken
        };

        public static JoinRoomResponse FromParameters(ParameterDictionary parameters) => new()
        {
            RoomName = parameters.Get<string>(ParamKey.RoomName),
            LocalActorId = parameters.Get<int>(ParamKey.ActorId),
            MasterClientId = parameters.Get<int>(ParamKey.MasterClientId),
            Players = PlayerInfo.ArrayFromObject(parameters[ParamKey.Players]),
            RoomProperties = parameters.Get<PropertyTable>(ParamKey.RoomProperties) ?? new PropertyTable(),
            ServerTimeTicks = parameters.Get<long>(ParamKey.ServerTimeTicks),
            MaxPlayers = parameters.Get<byte>(ParamKey.MaxPlayers),
            IsOpen = parameters.Get(ParamKey.IsOpen, true),
            IsVisible = parameters.Get(ParamKey.IsVisible, true),
            IsRejoin = parameters.Get<bool>(ParamKey.IsRejoin),
            TickRate = parameters.Get<int>(ParamKey.TickRate),
            RejoinToken = parameters.Get<string>(ParamKey.RejoinToken)
        };
    }

    public sealed class LeaveRoomRequest
    {
        public bool BecomeInactive { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.BecomeInactive] = BecomeInactive
        };

        public static LeaveRoomRequest FromParameters(ParameterDictionary parameters) => new()
        {
            BecomeInactive = parameters.Get<bool>(ParamKey.BecomeInactive)
        };
    }

    public sealed class RaiseEventRequest
    {
        public byte EventCode { get; set; }

        public object Data { get; set; }

        public ReceiverGroup Receivers { get; set; }

        public int[] TargetActors { get; set; }

        public byte InterestGroup { get; set; }

        public CachingOption Caching { get; set; }

        public DeliveryMode Delivery { get; set; }

        public int ViewId { get; set; }

        public ObjectOwnershipMode OwnershipMode { get; set; }

        public byte ObjectFlags { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.EventCode] = EventCode,
            [ParamKey.Data] = Data,
            [ParamKey.Receivers] = (byte)Receivers,
            [ParamKey.TargetActors] = TargetActors,
            [ParamKey.InterestGroup] = InterestGroup,
            [ParamKey.Caching] = (byte)Caching,
            [ParamKey.Delivery] = (byte)Delivery,
            [ParamKey.ViewId] = ViewId,
            [ParamKey.OwnershipMode] = (byte)OwnershipMode,
            [ParamKey.ObjectFlags] = ObjectFlags
        };

        public static RaiseEventRequest FromParameters(ParameterDictionary parameters) => new()
        {
            EventCode = parameters.Get<byte>(ParamKey.EventCode),
            Data = parameters[ParamKey.Data],
            Receivers = parameters.Get<ReceiverGroup>(ParamKey.Receivers),
            TargetActors = parameters.Get<int[]>(ParamKey.TargetActors),
            InterestGroup = parameters.Get<byte>(ParamKey.InterestGroup),
            Caching = parameters.Get<CachingOption>(ParamKey.Caching),
            Delivery = parameters.Get<DeliveryMode>(ParamKey.Delivery),
            ViewId = parameters.Get<int>(ParamKey.ViewId),
            OwnershipMode = parameters.Get<ObjectOwnershipMode>(ParamKey.OwnershipMode),
            ObjectFlags = parameters.Get<byte>(ParamKey.ObjectFlags)
        };
    }

    public sealed class SetPropertiesRequest
    {
        public int ActorId { get; set; }

        public PropertyTable Properties { get; set; }

        public PropertyTable ExpectedProperties { get; set; }

        public bool Broadcast { get; set; } = true;

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.ActorId] = ActorId,
            [ParamKey.Properties] = Properties,
            [ParamKey.ExpectedProperties] = ExpectedProperties,
            [ParamKey.Broadcast] = Broadcast
        };

        public static SetPropertiesRequest FromParameters(ParameterDictionary parameters) => new()
        {
            ActorId = parameters.Get<int>(ParamKey.ActorId),
            Properties = parameters.Get<PropertyTable>(ParamKey.Properties),
            ExpectedProperties = parameters.Get<PropertyTable>(ParamKey.ExpectedProperties),
            Broadcast = parameters.Get(ParamKey.Broadcast, true)
        };
    }

    public sealed class ChangeInterestGroupsRequest
    {
        public byte[] Add { get; set; }

        public byte[] Remove { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.GroupsToAdd] = Add,
            [ParamKey.GroupsToRemove] = Remove
        };

        public static ChangeInterestGroupsRequest FromParameters(ParameterDictionary parameters) => new()
        {
            Add = parameters.Get<byte[]>(ParamKey.GroupsToAdd),
            Remove = parameters.Get<byte[]>(ParamKey.GroupsToRemove)
        };
    }

    public sealed class TransferOwnershipRequest
    {
        public int ViewId { get; set; }

        public int NewOwnerActorId { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.ViewId] = ViewId,
            [ParamKey.NewOwnerActorId] = NewOwnerActorId
        };

        public static TransferOwnershipRequest FromParameters(ParameterDictionary parameters) => new()
        {
            ViewId = parameters.Get<int>(ParamKey.ViewId),
            NewOwnerActorId = parameters.Get<int>(ParamKey.NewOwnerActorId)
        };
    }

    public sealed class RequestOwnershipRequest
    {
        public int ViewId { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.ViewId] = ViewId
        };

        public static RequestOwnershipRequest FromParameters(ParameterDictionary parameters) => new()
        {
            ViewId = parameters.Get<int>(ParamKey.ViewId)
        };
    }

    public sealed class RegisterSceneObjectsRequest
    {
        public int[] ViewIds { get; set; } = System.Array.Empty<int>();

        public byte[] Modes { get; set; } = System.Array.Empty<byte>();

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.ViewIds] = ViewIds,
            [ParamKey.OwnershipModes] = Modes
        };

        public static RegisterSceneObjectsRequest FromParameters(ParameterDictionary parameters) => new()
        {
            ViewIds = parameters.Get<int[]>(ParamKey.ViewIds) ?? System.Array.Empty<int>(),
            Modes = parameters.Get<byte[]>(ParamKey.OwnershipModes) ?? System.Array.Empty<byte>()
        };
    }
}
