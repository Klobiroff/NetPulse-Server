using System.Net;
using LiteNetLib;
using LiteNetLib.Layers;
using NetPulse.Common.Security;

namespace NetPulse.TestClient;

public sealed class SecureClientLayer : PacketLayerBase
{
    private const byte ConnectRequestProperty = 5;
    private const byte UnconnectedMessageProperty = 8;

    [ThreadStatic]
    private static byte[]? ReplyBuffer;

    [ThreadStatic]
    private static byte[]? InitBuffer;

    private readonly SecureClientCore _core;
    private NetManager? _manager;
    private SecureEndpoint _server;
    private bool _serverKnown;

    public SecureClientLayer(SecureClientCore core)
        : base((core ?? throw new ArgumentNullException(nameof(core))).ExtraPacketSize) => _core = core;

    private static byte[] Reply => ReplyBuffer ??= new byte[2048];

    private static byte[] Scratch => InitBuffer ??= new byte[256];

    public void Attach(NetManager manager) => _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
        if (!_serverKnown || !SecureEndpoint.From(endPoint.Address, endPoint.Port).Equals(_server))
        {
            length = 0;
            return;
        }

        var result = _core.ProcessInbound(data, length, Reply);

        switch (result.Kind)
        {
            case SecureClientInboundKind.Decrypted:
                length = result.Length;
                return;
            case SecureClientInboundKind.Reply:
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

        if (_core.Session is not null)
        {
            var written = _core.Protect(data, offset, length);
            length = written < 0 ? 0 : written;
            return;
        }

        if ((data[offset] & 0x1F) != ConnectRequestProperty)
        {
            length = 0;
            return;
        }

        if (!_serverKnown)
        {
            _server = SecureEndpoint.From(endPoint.Address, endPoint.Port);
            _serverKnown = true;
        }

        _core.Stash(data, offset, length);
        var scratch = Scratch;
        var init = _core.BuildInit(scratch, 0);

        if (init <= 0)
        {
            length = 0;
            return;
        }

        if (offset + init > data.Length)
        {
            data = new byte[offset + init];
        }

        Buffer.BlockCopy(scratch, 0, data, offset, init);
        length = init;
    }
}
