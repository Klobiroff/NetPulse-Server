using NetPulse.Common;

namespace NetPulse.Server.Networking;

public static class Wire
{
    [ThreadStatic]
    private static NetWriter? CachedWriter;

    private static NetWriter Writer => CachedWriter ??= new NetWriter(1024);

    public static ArraySegment<byte> Event(byte code, ParameterDictionary? parameters) =>
        Protocol.Serialize(Writer, new EventData { Code = code, Parameters = parameters });

    public static ArraySegment<byte> Response(byte opCode, ushort requestId, short returnCode, string? message, ParameterDictionary? parameters) =>
        Protocol.Serialize(Writer, new OperationResponse
        {
            OpCode = opCode,
            RequestId = requestId,
            ReturnCode = returnCode,
            DebugMessage = message,
            Parameters = parameters
        });

    public static ArraySegment<byte> Pong(long clientTicks, long serverTicks) =>
        Protocol.SerializePingPong(Writer, MessageKind.Pong, clientTicks, serverTicks);
}
