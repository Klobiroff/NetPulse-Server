using System;

namespace NetPulse.Common.Security
{
    public static class X25519
    {
        public const int KeySize = 32;

        private const long A24 = 121665;

        private static long Operations;

        internal static long OperationCount => System.Threading.Interlocked.Read(ref Operations);

        public static byte[] GeneratePrivateKey() => SecureRandom.Bytes(KeySize);

        public static byte[] GetPublicKey(ReadOnlySpan<byte> privateKey)
        {
            var result = new byte[KeySize];
            Span<byte> basePoint = stackalloc byte[KeySize];
            basePoint.Clear();
            basePoint[0] = 9;
            ScalarMult(privateKey, basePoint, result);
            return result;
        }

        public static bool TryAgree(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey, Span<byte> shared)
        {
            ScalarMult(privateKey, peerPublicKey, shared);
            return !ConstantTime.IsZero(shared.Slice(0, KeySize));
        }

        public static void ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> u, Span<byte> result)
        {
            if (scalar.Length != KeySize || u.Length != KeySize || result.Length < KeySize)
            {
                throw new ArgumentException("X25519 operands must be 32 bytes");
            }

            System.Threading.Interlocked.Increment(ref Operations);

            Span<byte> k = stackalloc byte[KeySize];
            scalar.CopyTo(k);
            k[0] &= 248;
            k[31] &= 127;
            k[31] |= 64;

            Span<long> storage = stackalloc long[Fe25519.Limbs * 11];
            var x1 = storage.Slice(0, 10);
            var x2 = storage.Slice(10, 10);
            var z2 = storage.Slice(20, 10);
            var x3 = storage.Slice(30, 10);
            var z3 = storage.Slice(40, 10);
            var a = storage.Slice(50, 10);
            var aa = storage.Slice(60, 10);
            var b = storage.Slice(70, 10);
            var bb = storage.Slice(80, 10);
            var e = storage.Slice(90, 10);
            var c = storage.Slice(100, 10);

            Fe25519.FromBytes(x1, u);
            Fe25519.One(x2);
            Fe25519.Zero(z2);
            Fe25519.Copy(x3, x1);
            Fe25519.One(z3);

            Span<long> d = stackalloc long[Fe25519.Limbs];
            Span<long> da = stackalloc long[Fe25519.Limbs];
            Span<long> cb = stackalloc long[Fe25519.Limbs];
            long swap = 0;

            for (var t = 254; t >= 0; t--)
            {
                long bit = (k[t >> 3] >> (t & 7)) & 1;
                swap ^= bit;
                Fe25519.Swap(x2, x3, swap);
                Fe25519.Swap(z2, z3, swap);
                swap = bit;

                Fe25519.Add(a, x2, z2);
                Fe25519.Square(aa, a);
                Fe25519.Sub(b, x2, z2);
                Fe25519.Square(bb, b);
                Fe25519.Sub(e, aa, bb);
                Fe25519.Add(c, x3, z3);
                Fe25519.Sub(d, x3, z3);
                Fe25519.Mul(da, d, a);
                Fe25519.Mul(cb, c, b);
                Fe25519.Add(x3, da, cb);
                Fe25519.Square(x3, x3);
                Fe25519.Sub(z3, da, cb);
                Fe25519.Square(z3, z3);
                Fe25519.Mul(z3, z3, x1);
                Fe25519.Mul(x2, aa, bb);
                Fe25519.MulSmall(a, e, A24);
                Fe25519.Add(a, aa, a);
                Fe25519.Mul(z2, e, a);
            }

            Fe25519.Swap(x2, x3, swap);
            Fe25519.Swap(z2, z3, swap);
            Fe25519.Invert(z2, z2);
            Fe25519.Mul(x2, x2, z2);
            Fe25519.ToBytes(result, x2);

            storage.Clear();
            d.Clear();
            da.Clear();
            cb.Clear();
            k.Clear();
        }
    }
}
