namespace NetPulse.Common
{
    public enum DeliveryMode : byte
    {
        ReliableOrdered,
        ReliableUnordered,
        UnreliableSequenced,
        Unreliable
    }

    public enum MessageKind : byte
    {
        OperationRequest = 1,
        OperationResponse = 2,
        Event = 3,
        Ping = 4,
        Pong = 5
    }

    public enum ReceiverGroup : byte
    {
        Others,
        All,
        MasterClient,
        Targets
    }

    public enum CachingOption : byte
    {
        DoNotCache,
        AddToRoomCache,
        AddToRoomCacheGlobal,
        RemoveFromRoomCache,
        RemoveFromCacheForActorsLeft
    }

    public static class NetTypeCode
    {
        public const byte Null = 0;
        public const byte Bool = 1;
        public const byte Byte = 2;
        public const byte Short = 3;
        public const byte Int = 4;
        public const byte Long = 5;
        public const byte Float = 6;
        public const byte Double = 7;
        public const byte String = 8;
        public const byte ByteArray = 9;
        public const byte IntArray = 10;
        public const byte FloatArray = 11;
        public const byte StringArray = 12;
        public const byte ObjectArray = 13;
        public const byte Dictionary = 14;
        public const byte PropertyTable = 15;
        public const byte Guid = 16;
        public const byte ParameterDictionary = 17;
        public const byte Extended = 18;
        public const byte CustomMin = 100;
        public const byte CustomMax = 199;
    }

    public enum ObjectOwnershipMode : byte
    {
        Fixed,
        Takeover,
        Request
    }

    public static class ObjectFlag
    {
        public const byte PersistAcrossScenes = 1;
    }
}
