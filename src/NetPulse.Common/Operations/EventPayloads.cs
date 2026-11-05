namespace NetPulse.Common
{
    public sealed class RoomEventData
    {
        public int SenderActorId { get; set; }

        public object Data { get; set; }

        public int ViewId { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.Sender] = SenderActorId,
            [ParamKey.Data] = Data,
            [ParamKey.ViewId] = ViewId
        };

        public static RoomEventData FromParameters(ParameterDictionary parameters) => new()
        {
            SenderActorId = parameters.Get<int>(ParamKey.Sender),
            Data = parameters[ParamKey.Data],
            ViewId = parameters.Get<int>(ParamKey.ViewId)
        };
    }

    public sealed class PlayerLeftData
    {
        public int ActorId { get; set; }

        public bool IsInactive { get; set; }

        public int MasterClientId { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.ActorId] = ActorId,
            [ParamKey.IsInactive] = IsInactive,
            [ParamKey.MasterClientId] = MasterClientId
        };

        public static PlayerLeftData FromParameters(ParameterDictionary parameters) => new()
        {
            ActorId = parameters.Get<int>(ParamKey.ActorId),
            IsInactive = parameters.Get<bool>(ParamKey.IsInactive),
            MasterClientId = parameters.Get<int>(ParamKey.MasterClientId)
        };
    }

    public sealed class PropertiesChangedData
    {
        public int ActorId { get; set; }

        public PropertyTable Properties { get; set; }

        public int SenderActorId { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.ActorId] = ActorId,
            [ParamKey.Properties] = Properties,
            [ParamKey.Sender] = SenderActorId
        };

        public static PropertiesChangedData FromParameters(ParameterDictionary parameters) => new()
        {
            ActorId = parameters.Get<int>(ParamKey.ActorId),
            Properties = parameters.Get<PropertyTable>(ParamKey.Properties) ?? new PropertyTable(),
            SenderActorId = parameters.Get<int>(ParamKey.Sender)
        };
    }

    public sealed class OwnershipChangedData
    {
        public int ViewId { get; set; }

        public int NewOwnerActorId { get; set; }

        public int SenderActorId { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.ViewId] = ViewId,
            [ParamKey.NewOwnerActorId] = NewOwnerActorId,
            [ParamKey.Sender] = SenderActorId
        };

        public static OwnershipChangedData FromParameters(ParameterDictionary parameters) => new()
        {
            ViewId = parameters.Get<int>(ParamKey.ViewId),
            NewOwnerActorId = parameters.Get<int>(ParamKey.NewOwnerActorId),
            SenderActorId = parameters.Get<int>(ParamKey.Sender)
        };
    }

    public sealed class ErrorInfoData
    {
        public int ErrorCode { get; set; }

        public string Message { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.ErrorCode] = ErrorCode,
            [ParamKey.Message] = Message
        };

        public static ErrorInfoData FromParameters(ParameterDictionary parameters) => new()
        {
            ErrorCode = parameters.Get<int>(ParamKey.ErrorCode),
            Message = parameters.Get<string>(ParamKey.Message)
        };
    }

    public sealed class OwnershipRequestedData
    {
        public int ViewId { get; set; }

        public int RequesterActorId { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.ViewId] = ViewId,
            [ParamKey.RequesterActorId] = RequesterActorId
        };

        public static OwnershipRequestedData FromParameters(ParameterDictionary parameters) => new()
        {
            ViewId = parameters.Get<int>(ParamKey.ViewId),
            RequesterActorId = parameters.Get<int>(ParamKey.RequesterActorId)
        };
    }
}
