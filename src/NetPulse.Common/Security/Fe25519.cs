using System;

namespace NetPulse.Common.Security
{
    internal static class Fe25519
    {
        public const int Limbs = 10;

        private const long Round25 = 1L << 24;
        private const long Round26 = 1L << 25;

        public static void Zero(Span<long> h) => h.Clear();

        public static void One(Span<long> h)
        {
            h.Clear();
            h[0] = 1;
        }

        public static void Copy(Span<long> h, ReadOnlySpan<long> f) => f.CopyTo(h);

        public static void FromBytes(Span<long> h, ReadOnlySpan<byte> s)
        {
            Span<byte> padded = stackalloc byte[40];
            padded.Clear();
            s.Slice(0, 32).CopyTo(padded);
            padded[31] &= 0x7F;

            for (var i = 0; i < Limbs; i++)
            {
                var position = (51 * i + 1) / 2;
                var width = (i & 1) == 0 ? 26 : 25;
                var index = position >> 3;
                var shift = position & 7;
                ulong chunk = 0;

                for (var b = 0; b < 8; b++)
                {
                    chunk |= (ulong)padded[index + b] << (8 * b);
                }

                h[i] = (long)((chunk >> shift) & ((1UL << width) - 1));
            }

            padded.Clear();
        }

        public static void ToBytes(Span<byte> s, ReadOnlySpan<long> f)
        {
            Span<long> h = stackalloc long[Limbs];
            f.CopyTo(h);
            Carry(h);

            var q = (19 * h[9] + Round25) >> 25;
            q = (h[0] + q) >> 26;
            q = (h[1] + q) >> 25;
            q = (h[2] + q) >> 26;
            q = (h[3] + q) >> 25;
            q = (h[4] + q) >> 26;
            q = (h[5] + q) >> 25;
            q = (h[6] + q) >> 26;
            q = (h[7] + q) >> 25;
            q = (h[8] + q) >> 26;
            q = (h[9] + q) >> 25;

            h[0] += 19 * q;

            for (var i = 0; i < Limbs - 1; i++)
            {
                var shift = (i & 1) == 0 ? 26 : 25;
                var carry = h[i] >> shift;
                h[i + 1] += carry;
                h[i] -= carry << shift;
            }

            h[9] &= (1L << 25) - 1;

            ulong accumulator = 0;
            var bits = 0;
            var written = 0;

            for (var i = 0; i < Limbs; i++)
            {
                accumulator |= (ulong)h[i] << bits;
                bits += (i & 1) == 0 ? 26 : 25;

                while (bits >= 8)
                {
                    s[written++] = (byte)accumulator;
                    accumulator >>= 8;
                    bits -= 8;
                }
            }

            s[written] = (byte)accumulator;
            h.Clear();
        }

        public static void Add(Span<long> h, ReadOnlySpan<long> f, ReadOnlySpan<long> g)
        {
            for (var i = 0; i < Limbs; i++)
            {
                h[i] = f[i] + g[i];
            }
        }

        public static void Sub(Span<long> h, ReadOnlySpan<long> f, ReadOnlySpan<long> g)
        {
            for (var i = 0; i < Limbs; i++)
            {
                h[i] = f[i] - g[i];
            }
        }

        public static void Carry(Span<long> h)
        {
            var c = (h[0] + Round26) >> 26;
            h[1] += c;
            h[0] -= c << 26;
            c = (h[4] + Round26) >> 26;
            h[5] += c;
            h[4] -= c << 26;
            c = (h[1] + Round25) >> 25;
            h[2] += c;
            h[1] -= c << 25;
            c = (h[5] + Round25) >> 25;
            h[6] += c;
            h[5] -= c << 25;
            c = (h[2] + Round26) >> 26;
            h[3] += c;
            h[2] -= c << 26;
            c = (h[6] + Round26) >> 26;
            h[7] += c;
            h[6] -= c << 26;
            c = (h[3] + Round25) >> 25;
            h[4] += c;
            h[3] -= c << 25;
            c = (h[7] + Round25) >> 25;
            h[8] += c;
            h[7] -= c << 25;
            c = (h[4] + Round26) >> 26;
            h[5] += c;
            h[4] -= c << 26;
            c = (h[8] + Round26) >> 26;
            h[9] += c;
            h[8] -= c << 26;
            c = (h[9] + Round25) >> 25;
            h[0] += c * 19;
            h[9] -= c << 25;
            c = (h[0] + Round26) >> 26;
            h[1] += c;
            h[0] -= c << 26;
        }

        public static void Mul(Span<long> h, ReadOnlySpan<long> f, ReadOnlySpan<long> g)
        {
            Span<long> t = stackalloc long[Limbs];
            t.Clear();

            for (var i = 0; i < Limbs; i++)
            {
                var fi = f[i];

                for (var j = 0; j < Limbs; j++)
                {
                    var product = fi * g[j];

                    if ((i & j & 1) == 1)
                    {
                        product += product;
                    }

                    var k = i + j;

                    if (k >= Limbs)
                    {
                        product *= 19;
                        k -= Limbs;
                    }

                    t[k] += product;
                }
            }

            t.CopyTo(h);
            Carry(h);
            t.Clear();
        }

        public static void Square(Span<long> h, ReadOnlySpan<long> f) => Mul(h, f, f);

        public static void MulSmall(Span<long> h, ReadOnlySpan<long> f, long constant)
        {
            for (var i = 0; i < Limbs; i++)
            {
                h[i] = f[i] * constant;
            }

            Carry(h);
        }

        public static void Swap(Span<long> f, Span<long> g, long bit)
        {
            var mask = -bit;

            for (var i = 0; i < Limbs; i++)
            {
                var x = mask & (f[i] ^ g[i]);
                f[i] ^= x;
                g[i] ^= x;
            }
        }

        public static void Invert(Span<long> output, ReadOnlySpan<long> z)
        {
            Span<long> t0 = stackalloc long[Limbs];
            Span<long> t1 = stackalloc long[Limbs];
            Span<long> t2 = stackalloc long[Limbs];
            Span<long> t3 = stackalloc long[Limbs];

            Square(t0, z);
            Square(t1, t0);
            Square(t1, t1);
            Mul(t1, z, t1);
            Mul(t0, t0, t1);
            Square(t2, t0);
            Mul(t1, t1, t2);
            SquareRepeated(t2, t1, 5);
            Mul(t1, t2, t1);
            SquareRepeated(t2, t1, 10);
            Mul(t2, t2, t1);
            SquareRepeated(t3, t2, 20);
            Mul(t2, t3, t2);
            SquareRepeated(t2, t2, 10);
            Mul(t1, t2, t1);
            SquareRepeated(t2, t1, 50);
            Mul(t2, t2, t1);
            SquareRepeated(t3, t2, 100);
            Mul(t2, t3, t2);
            SquareRepeated(t2, t2, 50);
            Mul(t1, t2, t1);
            SquareRepeated(t1, t1, 5);
            Mul(output, t1, t0);

            t0.Clear();
            t1.Clear();
            t2.Clear();
            t3.Clear();
        }

        private static void SquareRepeated(Span<long> output, ReadOnlySpan<long> input, int count)
        {
            Square(output, input);

            for (var i = 1; i < count; i++)
            {
                Square(output, output);
            }
        }
    }
}
