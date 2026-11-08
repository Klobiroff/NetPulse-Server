using System.Net;
using LiteNetLib;
using LiteNetLib.Layers;
using NetPulse.Common.Security;

namespace NetPulse.Server.Networking;

public sealed class SecureTransportLayer : PacketLayerBase
{
    private const byte UnconnectedMessageProperty = 8;

    [ThreadStatic]
    private static byte[]? ReplyBuffer;

    private readonly SecureServerCore _core;
    private NetManager? _manager;

    public SecureTransportLayer(SecureServerCore core)
        : base((core ?? throw new ArgumentNullException(nameof(core))).ExtraPacketSize) => _core = core;

    private static byte[] Reply => ReplyBuffer ??= new byte[512];

    public void Attach(NetManager manager) => _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
        var key = SecureEndpoint.From(endPoint.Address, endPoint.Port);
        var result = _core.ProcessInbound(key, data, length, Reply);

        switch (result.Kind)
        {
            case SecureInboundKind.Plain:
                return;
            case SecureInboundKind.Decrypted:
                length = result.Length;
                return;
            case SecureInboundKind.Reply:
                _manager?.SendUnconnectedMessage(Reply, 0, result.Length, endPoint);
                length = 0;
                return;
            default:
                length = 0;
                return;
        }
    }

    public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
    {
        if (endPoint is null)
        {
            length = 0;
            return;
        }

        if (length >= 2 && (data[offset] & 0x1F) == UnconnectedMessageProperty && SecureRecord.IsRecord(data[offset + 1]))
        {
            offset++;
            length--;
            return;
        }

        var key = SecureEndpoint.From(endPoint.Address, endPoint.Port);
        var written = _core.ProtectOutbound(key, data, offset, length);

        if (written >= 0)
        {
            length = written;
            return;
        }

        if (written == -1 && _core.AllowPlaintext)
        {
            return;
        }

        length = 0;
    }
}
