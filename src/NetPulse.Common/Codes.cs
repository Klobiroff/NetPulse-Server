namespace NetPulse.Common
{
    public static class OpCode
    {
        public const byte Authenticate = 1;
        public const byte JoinLobby = 2;
        public const byte LeaveLobby = 3;
        public const byte CreateRoom = 10;
        public const byte JoinRoom = 11;
        public const byte JoinOrCreateRoom = 12;
        public const byte JoinRandomRoom = 13;
        public const byte LeaveRoom = 14;
        public const byte RaiseEvent = 20;
        public const byte SetRoomProperties = 21;
        public const byte SetPlayerProperties = 22;
        public const byte ChangeInterestGroups = 23;
        public const byte TransferOwnership = 24;
        public const byte GetRoomList = 30;
        public const byte RequestOwnership = 25;
        public const byte RegisterSceneObjects = 26;
    }

    public static class EventCode
    {
        public const byte RoomList = 230;
        public const byte PlayerJoined = 231;
        public const byte PlayerLeft = 232;
        public const byte RoomPropertiesChanged = 233;
        public const byte PlayerPropertiesChanged = 234;
        public const byte MasterClientChanged = 235;
        public const byte OwnershipChanged = 236;
        public const byte OwnershipRequested = 237;
        public const byte ObjectInstantiate = 240;
        public const byte ObjectDestroy = 241;
        public const byte Rpc = 242;
        public const byte StateSync = 243;
        public const byte Message = 244;
        public const byte SyncVarSet = 245;
        public const byte ErrorInfo = 250;

        public const byte MaxUserEvent = 199;
    }

    public static class ReturnCode
    {
        public const short Ok = 0;
        public const short InternalError = -1;
        public const short InvalidRequest = -2;
        public const short NotAuthenticated = -3;
        public const short NotInRoom = -4;
        public const short AlreadyInRoom = -5;
        public const short Forbidden = -6;
        public const short RoomNotFound = -10;
        public const short RoomFull = -11;
        public const short RoomClosed = -12;
        public const short RoomAlreadyExists = -13;
        public const short NoMatchFound = -14;
        public const short PropertyCasFailed = -15;
        public const short ActorNotFound = -16;
        public const short ObjectNotFound = -17;
        public const short PluginReportedError = -20;
        public const short PluginNotFound = -21;
        public const short VersionMismatch = -30;
        public const short InvalidAppId = -31;
        public const short AuthenticationFailed = -32;
    }

    public static class ParamKey
    {
        public const byte AppId = 1;
        public const byte ProtocolVersion = 2;
        public const byte AppVersion = 3;
        public const byte UserId = 4;
        public const byte NickName = 5;
        public const byte AuthToken = 6;
        public const byte ServerTimeTicks = 7;
        public const byte AuthData = 8;

        public const byte RoomName = 10;
        public const byte Options = 11;
        public const byte PlayerProperties = 12;
        public const byte ActorId = 13;
        public const byte MasterClientId = 14;
        public const byte Players = 15;
        public const byte RoomProperties = 16;
        public const byte Properties = 17;
        public const byte ExpectedProperties = 18;
        public const byte ExpectedMaxPlayers = 19;
        public const byte LobbyName = 20;
        public const byte BecomeInactive = 21;
        public const byte Broadcast = 22;
        public const byte IsRejoin = 23;
        public const byte IsOpen = 24;
        public const byte IsVisible = 25;
        public const byte MaxPlayers = 26;
        public const byte Plugin = 27;
        public const byte PlayerCount = 28;
        public const byte LobbyProperties = 29;

        public const byte EventCode = 30;
        public const byte Data = 31;
        public const byte Receivers = 32;
        public const byte TargetActors = 33;
        public const byte InterestGroup = 34;
        public const byte Caching = 35;
        public const byte Delivery = 36;
        public const byte Sender = 37;
        public const byte ViewId = 38;
        public const byte NewOwnerActorId = 39;
        public const byte GroupsToAdd = 40;
        public const byte GroupsToRemove = 41;
        public const byte PlayerTtlMs = 42;
        public const byte EmptyRoomTtlMs = 43;
        public const byte OwnershipMode = 44;
        public const byte ViewIds = 45;
        public const byte OwnershipModes = 46;
        public const byte RequesterActorId = 47;
        public const byte ObjectFlags = 48;
        public const byte TickRate = 49;

        public const byte IsFullList = 50;
        public const byte Rooms = 51;
        public const byte RemovedRooms = 52;
        public const byte IsInactive = 53;
        public const byte ErrorCode = 54;
        public const byte Message = 55;
    }
}
