using System;

namespace NetPulse.Common
{
    public interface INetTransport
    {
        event Action Connected;

        event Action<string> Disconnected;

        event Action<ArraySegment<byte>, DeliveryMode> DataReceived;

        bool IsConnected { get; }

        int RoundTripTimeMs { get; }

        void Connect(string host, int port, string connectionKey);

        void Send(ArraySegment<byte> data, DeliveryMode mode);

        void Poll();

        void Disconnect();
    }
}
