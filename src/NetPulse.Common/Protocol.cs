using System;

namespace NetPulse.Common
{
    public struct OperationRequest
    {
        public byte OpCode { get; set; }

        public ushort RequestId { get; set; }

        public ParameterDictionary Parameters { get; set; }
    }

    public struct OperationResponse
    {
        public byte OpCode { get; set; }

        public ushort RequestId { get; set; }

        public short ReturnCode { get; set; }

        public string DebugMessage { get; set; }

        public ParameterDictionary Parameters { get; set; }
    }

    public struct EventData
    {
        public byte Code { get; set; }

        public ParameterDictionary Parameters { get; set; }
    }

    public static class Protocol
    {
        public const ushort ProtocolVersion = 1;
        public const int DefaultPort = 5055;
        public const string DefaultConnectionKey = "NetPulse";

        public static ArraySegment<byte> Serialize(NetWriter writer, OperationRequest request)
        {
            writer.Reset();
            writer.WriteByte((byte)MessageKind.OperationRequest);
            writer.WriteByte(request.OpCode);
            writer.WriteUShort(request.RequestId);
            WriteParameters(writer, request.Parameters);
            return writer.AsSegment();
        }

        public static ArraySegment<byte> Serialize(NetWriter writer, OperationResponse response)
        {
            writer.Reset();
            writer.WriteByte((byte)MessageKind.OperationResponse);
            writer.WriteByte(response.OpCode);
            writer.WriteUShort(response.RequestId);
            writer.WriteShort(response.ReturnCode);
            writer.WriteString(response.DebugMessage);
            WriteParameters(writer, response.Parameters);
            return writer.AsSegment();
        }

        public static ArraySegment<byte> Serialize(NetWriter writer, EventData data)
        {
            writer.Reset();
            writer.WriteByte((byte)MessageKind.Event);
            writer.WriteByte(data.Code);
            WriteParameters(writer, data.Parameters);
            return writer.AsSegment();
        }

        public static ArraySegment<byte> SerializePingPong(NetWriter writer, MessageKind kind, long clientTicks, long serverTicks)
        {
            if (kind is not (MessageKind.Ping or MessageKind.Pong))
            {
                throw new ArgumentException("Kind must be Ping or Pong.", nameof(kind));
            }

            writer.Reset();
            writer.WriteByte((byte)kind);
            writer.WriteLong(clientTicks);
            writer.WriteLong(serverTicks);
            return writer.AsSegment();
        }

        public static MessageKind ReadKind(NetReader reader) => (MessageKind)reader.ReadByte();

        public static OperationRequest ReadOperationRequest(NetReader reader) => new()
        {
            OpCode = reader.ReadByte(),
            RequestId = reader.ReadUShort(),
            Parameters = ParameterDictionary.ReadFrom(reader)
        };

        public static OperationResponse ReadOperationResponse(NetReader reader) => new()
        {
            OpCode = reader.ReadByte(),
            RequestId = reader.ReadUShort(),
            ReturnCode = reader.ReadShort(),
            DebugMessage = reader.ReadString(),
            Parameters = ParameterDictionary.ReadFrom(reader)
        };

        public static EventData ReadEvent(NetReader reader) => new()
        {
            Code = reader.ReadByte(),
            Parameters = ParameterDictionary.ReadFrom(reader)
        };

        public static void ReadPingPong(NetReader reader, out long clientTicks, out long serverTicks)
        {
            clientTicks = reader.ReadLong();
            serverTicks = reader.ReadLong();
        }

        private static void WriteParameters(NetWriter writer, ParameterDictionary parameters)
        {
            if (parameters == null)
            {
                writer.WriteVarUInt(0);
                return;
            }

            parameters.WriteTo(writer);
        }
    }
}
