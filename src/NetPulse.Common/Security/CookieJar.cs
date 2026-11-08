using System;
using System.Buffers.Binary;

namespace NetPulse.Common.Security
{
    public sealed class CookieJar
    {
        private readonly long _lifetimeMs;
        private readonly object _gate = new();
        private volatile Generation _generation;

        public CookieJar(int lifetimeMs, long nowMs)
        {
            if (lifetimeMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(lifetimeMs));
            }

            _lifetimeMs = lifetimeMs;
            _generation = new Generation(0, nowMs + lifetimeMs, SecureRandom.Bytes(32), SecureRandom.Bytes(32));
        }

        public void Compute(in SecureEndpoint endpoint, long nowMs, Span<byte> cookie)
        {
            var generation = Current(nowMs);
            Compute(generation.Current, generation.Epoch, endpoint, cookie);
        }

        public bool Verify(in SecureEndpoint endpoint, ReadOnlySpan<byte> cookie, long nowMs)
        {
            if (cookie.Length != SecureRecord.CookieSize)
            {
                return false;
            }

            var generation = Current(nowMs);
            Span<byte> current = stackalloc byte[SecureRecord.CookieSize];
            Span<byte> previous = stackalloc byte[SecureRecord.CookieSize];
            Compute(generation.Current, generation.Epoch, endpoint, current);
            Compute(generation.Previous, generation.Epoch - 1, endpoint, previous);

            var matchCurrent = ConstantTime.AreEqual(current, cookie);
            var matchPrevious = ConstantTime.AreEqual(previous, cookie);
            return matchCurrent | matchPrevious;
        }

        private static void Compute(byte[] secret, long epoch, in SecureEndpoint endpoint, Span<byte> cookie)
        {
            Span<byte> input = stackalloc byte[SecureEndpoint.ByteSize + 8];
            endpoint.Write(input);
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(SecureEndpoint.ByteSize, 8), epoch);

            Span<byte> mac = stackalloc byte[HmacSha256.MacSize];
            HmacSha256.Compute(secret, input, mac);
            mac.Slice(0, SecureRecord.CookieSize).CopyTo(cookie);
            mac.Clear();
        }

        private Generation Current(long nowMs)
        {
            var generation = _generation;

            if (nowMs < generation.NextRotationMs)
            {
                return generation;
            }

            lock (_gate)
            {
                generation = _generation;

                if (nowMs < generation.NextRotationMs)
                {
                    return generation;
                }

                var steps = ((nowMs - generation.NextRotationMs) / _lifetimeMs) + 1;
                var previous = steps == 1 ? generation.Current : SecureRandom.Bytes(32);
                ConstantTime.Zero(generation.Previous);

                if (steps != 1)
                {
                    ConstantTime.Zero(generation.Current);
                }

                _generation = new Generation(
                    generation.Epoch + steps,
                    generation.NextRotationMs + (steps * _lifetimeMs),
                    SecureRandom.Bytes(32),
                    previous);

                return _generation;
            }
        }

        private sealed class Generation
        {
            public Generation(long epoch, long nextRotationMs, byte[] current, byte[] previous)
            {
                Epoch = epoch;
                NextRotationMs = nextRotationMs;
                Current = current;
                Previous = previous;
            }

            public long Epoch { get; }

            public long NextRotationMs { get; }

            public byte[] Current { get; }

            public byte[] Previous { get; }
        }
    }
}
