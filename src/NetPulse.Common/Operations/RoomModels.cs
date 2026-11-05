using System;
using System.Collections.Generic;

namespace NetPulse.Common
{
    public sealed class RoomOptions
    {
        public byte MaxPlayers { get; set; }

        public bool IsVisible { get; set; } = true;

        public bool IsOpen { get; set; } = true;

        public PropertyTable CustomProperties { get; set; }

        public string[] LobbyProperties { get; set; }

        public string Plugin { get; set; }

        public int PlayerTtlMs { get; set; }

        public int EmptyRoomTtlMs { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.MaxPlayers] = MaxPlayers,
            [ParamKey.IsVisible] = IsVisible,
            [ParamKey.IsOpen] = IsOpen,
            [ParamKey.Properties] = CustomProperties,
            [ParamKey.LobbyProperties] = LobbyProperties,
            [ParamKey.Plugin] = Plugin,
            [ParamKey.PlayerTtlMs] = PlayerTtlMs,
            [ParamKey.EmptyRoomTtlMs] = EmptyRoomTtlMs
        };

        public static RoomOptions FromParameters(ParameterDictionary parameters) => parameters == null
            ? null
            : new()
            {
                MaxPlayers = parameters.Get<byte>(ParamKey.MaxPlayers),
                IsVisible = parameters.Get(ParamKey.IsVisible, true),
                IsOpen = parameters.Get(ParamKey.IsOpen, true),
                CustomProperties = parameters.Get<PropertyTable>(ParamKey.Properties),
                LobbyProperties = parameters.Get<string[]>(ParamKey.LobbyProperties),
                Plugin = parameters.Get<string>(ParamKey.Plugin),
                PlayerTtlMs = parameters.Get<int>(ParamKey.PlayerTtlMs),
                EmptyRoomTtlMs = parameters.Get<int>(ParamKey.EmptyRoomTtlMs)
            };
    }

    public sealed class PlayerInfo
    {
        public int ActorId { get; set; }

        public string NickName { get; set; }

        public string UserId { get; set; }

        public PropertyTable Properties { get; set; }

        public bool IsInactive { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.ActorId] = ActorId,
            [ParamKey.NickName] = NickName,
            [ParamKey.UserId] = UserId,
            [ParamKey.Properties] = Properties,
            [ParamKey.IsInactive] = IsInactive
        };

        public static PlayerInfo FromParameters(ParameterDictionary parameters) => new()
        {
            ActorId = parameters.Get<int>(ParamKey.ActorId),
            NickName = parameters.Get<string>(ParamKey.NickName),
            UserId = parameters.Get<string>(ParamKey.UserId),
            Properties = parameters.Get<PropertyTable>(ParamKey.Properties),
            IsInactive = parameters.Get<bool>(ParamKey.IsInactive)
        };

        public static object[] ToObjectArray(IReadOnlyList<PlayerInfo> players)
        {
            var result = new object[players.Count];

            for (var i = 0; i < result.Length; i++)
            {
                result[i] = players[i].ToParameters();
            }

            return result;
        }

        public static PlayerInfo[] ArrayFromObject(object value)
        {
            if (value is not object[] items)
            {
                return Array.Empty<PlayerInfo>();
            }

            var result = new PlayerInfo[items.Length];

            for (var i = 0; i < items.Length; i++)
            {
                result[i] = FromParameters((ParameterDictionary)items[i]);
            }

            return result;
        }
    }

    public sealed class RoomInfo
    {
        public string Name { get; set; }

        public byte PlayerCount { get; set; }

        public byte MaxPlayers { get; set; }

        public bool IsOpen { get; set; }

        public PropertyTable LobbyProperties { get; set; }

        public ParameterDictionary ToParameters() => new()
        {
            [ParamKey.RoomName] = Name,
            [ParamKey.PlayerCount] = PlayerCount,
            [ParamKey.MaxPlayers] = MaxPlayers,
            [ParamKey.IsOpen] = IsOpen,
            [ParamKey.Properties] = LobbyProperties
        };

        public static RoomInfo FromParameters(ParameterDictionary parameters) => new()
        {
            Name = parameters.Get<string>(ParamKey.RoomName),
            PlayerCount = parameters.Get<byte>(ParamKey.PlayerCount),
            MaxPlayers = parameters.Get<byte>(ParamKey.MaxPlayers),
            IsOpen = parameters.Get<bool>(ParamKey.IsOpen),
            LobbyProperties = parameters.Get<PropertyTable>(ParamKey.Properties)
        };
    }

    public sealed class RoomListData
    {
        public bool IsFullList { get; set; }

        public RoomInfo[] Rooms { get; set; } = Array.Empty<RoomInfo>();

        public string[] RemovedRooms { get; set; } = Array.Empty<string>();

        public ParameterDictionary ToParameters()
        {
            var items = new object[Rooms.Length];

            for (var i = 0; i < items.Length; i++)
            {
                items[i] = Rooms[i].ToParameters();
            }

            return new()
            {
                [ParamKey.IsFullList] = IsFullList,
                [ParamKey.Rooms] = items,
                [ParamKey.RemovedRooms] = RemovedRooms
            };
        }

        public static RoomListData FromParameters(ParameterDictionary parameters)
        {
            var result = new RoomListData
            {
                IsFullList = parameters.Get<bool>(ParamKey.IsFullList),
                RemovedRooms = parameters.Get<string[]>(ParamKey.RemovedRooms) ?? Array.Empty<string>()
            };

            if (parameters[ParamKey.Rooms] is object[] items)
            {
                result.Rooms = new RoomInfo[items.Length];

                for (var i = 0; i < items.Length; i++)
                {
                    result.Rooms[i] = RoomInfo.FromParameters((ParameterDictionary)items[i]);
                }
            }

            return result;
        }
    }
}
