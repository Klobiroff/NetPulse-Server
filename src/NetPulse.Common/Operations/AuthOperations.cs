namespace NetPulse.Common
{
    public sealed class AuthenticateRequest
    {
        public string AppId { get; set; }

        public ushort ProtocolVersion { get; set; } = Protocol.ProtocolVersion;

        public string AppVersion { get; set; }

        public string UserId { get; set; }

        public string NickName { get; set; }

        public string AuthToken { get; set; }

        public PropertyTable AuthData { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.AppId] = AppId,
            [ParamKey.ProtocolVersion] = (int)ProtocolVersion,
            [ParamKey.AppVersion] = AppVersion,
            [ParamKey.UserId] = UserId,
            [ParamKey.NickName] = NickName,
            [ParamKey.AuthToken] = AuthToken,
            [ParamKey.AuthData] = AuthData
        };

        public static AuthenticateRequest FromParameters(ParameterDictionary parameters) => new()
        {
            AppId = parameters.Get<string>(ParamKey.AppId),
            ProtocolVersion = parameters.Get<ushort>(ParamKey.ProtocolVersion),
            AppVersion = parameters.Get<string>(ParamKey.AppVersion),
            UserId = parameters.Get<string>(ParamKey.UserId),
            NickName = parameters.Get<string>(ParamKey.NickName),
            AuthToken = parameters.Get<string>(ParamKey.AuthToken),
            AuthData = parameters.Get<PropertyTable>(ParamKey.AuthData)
        };
    }

    public sealed class AuthenticateResponse
    {
        public string UserId { get; set; }

        public string NickName { get; set; }

        public long ServerTimeTicks { get; set; }

        public int ServerProtocolVersion { get; set; } = Protocol.ProtocolVersion;

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.UserId] = UserId,
            [ParamKey.NickName] = NickName,
            [ParamKey.ServerTimeTicks] = ServerTimeTicks,
            [ParamKey.ProtocolVersion] = ServerProtocolVersion
        };

        public static AuthenticateResponse FromParameters(ParameterDictionary parameters) => new()
        {
            UserId = parameters.Get<string>(ParamKey.UserId),
            NickName = parameters.Get<string>(ParamKey.NickName),
            ServerTimeTicks = parameters.Get<long>(ParamKey.ServerTimeTicks),
            ServerProtocolVersion = parameters.Get<int>(ParamKey.ProtocolVersion)
        };
    }

    public sealed class JoinLobbyRequest
    {
        public string LobbyName { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.LobbyName] = LobbyName
        };

        public static JoinLobbyRequest FromParameters(ParameterDictionary parameters) => new()
        {
            LobbyName = parameters.Get<string>(ParamKey.LobbyName)
        };
    }
}
