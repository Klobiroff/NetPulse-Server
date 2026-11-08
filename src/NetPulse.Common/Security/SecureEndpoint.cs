using System;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace NetPulse.Common.Security
{
    public readonly struct SecureEndpoint : IEquatable<SecureEndpoint>
    {
        public const int ByteSize = 18;

        private static readonly ulong Seed = CreateSeed();

        private readonly ulong _high;
        private readonly ulong _low;
        private readonly ushort _port;

        private SecureEndpoint(ulong high, ulong low, ushort port)
        {
            _high = high;
            _low = low;
            _port = port;
        }

        public int Port => _port;

        public SecureEndpoint AddressOnly => new SecureEndpoint(_high, _low, 0);

        public static SecureEndpoint From(IPAddress address, int port)
        {
            Span<byte> bytes = stackalloc byte[16];
            bytes.Clear();

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                bytes[10] = 0xFF;
                bytes[11] = 0xFF;
                address.TryWriteBytes(bytes.Slice(12, 4), out _);
            }
            else
            {
                address.TryWriteBytes(bytes, out _);
            }

            return new SecureEndpoint(
                BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(0, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8, 8)),
                (ushort)port);
        }

        public void Write(Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(0, 8), _high);
            BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8, 8), _low);
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(16, 2), _port);
        }

        public bool Equals(SecureEndpoint other) => _high == other._high && _low == other._low && _port == other._port;

        public override bool Equals(object obj) => obj is SecureEndpoint other && Equals(other);

        public override int GetHashCode()
        {
            var value = _high ^ Seed;
            value = Mix(value + _low);
            value = Mix(value + _port);
            return (int)(value ^ (value >> 32));
        }

        public override string ToString() => $"{_high:x16}{_low:x16}:{_port}";

        private static ulong Mix(ulong value)
        {
            value ^= value >> 30;
            value *= 0xbf58476d1ce4e5b9UL;
            value ^= value >> 27;
            value *= 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }

        private static ulong CreateSeed()
        {
            var bytes = new byte[8];

            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            return BitConverter.ToUInt64(bytes, 0);
        }
    }
}
