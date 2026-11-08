using System;
using System.Buffers.Binary;

namespace NetPulse.Common.Security
{
    public static class ChaCha20Poly1305
    {
        public const int KeySize = 32;
        public const int NonceSize = 12;
        public const int TagSize = 16;

        public static void Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, Span<byte> data, Span<byte> tag)
        {
            Check(key, nonce, tag);

            Span<byte> polyKey = stackalloc byte[KeySize];
            DerivePolyKey(key, nonce, polyKey);
            Xor(key, nonce, data);
            Poly1305.Authenticate(polyKey, aad, data, tag);
            polyKey.Clear();
        }

        public static bool Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, Span<byte> data, ReadOnlySpan<byte> tag)
        {
            Check(key, nonce, tag);

            Span<byte> polyKey = stackalloc byte[KeySize];
            Span<byte> expected = stackalloc byte[TagSize];
            DerivePolyKey(key, nonce, polyKey);
            Poly1305.Authenticate(polyKey, aad, data, expected);
            polyKey.Clear();

            var valid = ConstantTime.AreEqual(expected, tag);
            expected.Clear();

            if (valid)
            {
                Xor(key, nonce, data);
            }

            return valid;
        }

        public static bool Verify(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag)
        {
            Check(key, nonce, tag);

            Span<byte> polyKey = stackalloc byte[KeySize];
            Span<byte> expected = stackalloc byte[TagSize];
            DerivePolyKey(key, nonce, polyKey);
            Poly1305.Authenticate(polyKey, aad, data, expected);
            polyKey.Clear();

            var valid = ConstantTime.AreEqual(expected, tag);
            expected.Clear();
            return valid;
        }

        private static void Check(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> tag)
        {
            if (key.Length != KeySize || nonce.Length != NonceSize || tag.Length != TagSize)
            {
                throw new ArgumentException("Invalid ChaCha20-Poly1305 parameter size");
            }
        }

        private static void DerivePolyKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, Span<byte> polyKey)
        {
            Span<byte> block = stackalloc byte[64];
            ChaCha20.Block(key, nonce, 0, block);
            block.Slice(0, KeySize).CopyTo(polyKey);
            block.Clear();
        }

        private static void Xor(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, Span<byte> data)
        {
            Span<byte> block = stackalloc byte[64];
            uint counter = 1;
            var offset = 0;

            while (offset < data.Length)
            {
                ChaCha20.Block(key, nonce, counter++, block);
                var count = Math.Min(64, data.Length - offset);

                for (var i = 0; i < count; i++)
                {
                    data[offset + i] ^= block[i];
                }

                offset += count;
            }

            block.Clear();
        }
    }

    internal static class ChaCha20
    {
        public static void Block(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter, Span<byte> output)
        {
            Span<uint> state = stackalloc uint[16];
            Span<uint> work = stackalloc uint[16];

            state[0] = 0x61707865;
            state[1] = 0x3320646e;
            state[2] = 0x79622d32;
            state[3] = 0x6b206574;

            for (var i = 0; i < 8; i++)
            {
                state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));
            }

            state[12] = counter;
            state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(0, 4));
            state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4, 4));
            state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8, 4));

            state.CopyTo(work);

            for (var round = 0; round < 10; round++)
            {
                QuarterRound(work, 0, 4, 8, 12);
                QuarterRound(work, 1, 5, 9, 13);
                QuarterRound(work, 2, 6, 10, 14);
                QuarterRound(work, 3, 7, 11, 15);
                QuarterRound(work, 0, 5, 10, 15);
                QuarterRound(work, 1, 6, 11, 12);
                QuarterRound(work, 2, 7, 8, 13);
                QuarterRound(work, 3, 4, 9, 14);
            }

            for (var i = 0; i < 16; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4, 4), work[i] + state[i]);
            }

            state.Clear();
            work.Clear();
        }

        private static uint Rotate(uint value, int bits) => (value << bits) | (value >> (32 - bits));

        private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
        {
            var xa = x[a];
            var xb = x[b];
            var xc = x[c];
            var xd = x[d];

            xa += xb;
            xd = Rotate(xd ^ xa, 16);
            xc += xd;
            xb = Rotate(xb ^ xc, 12);
            xa += xb;
            xd = Rotate(xd ^ xa, 8);
            xc += xd;
            xb = Rotate(xb ^ xc, 7);

            x[a] = xa;
            x[b] = xb;
            x[c] = xc;
            x[d] = xd;
        }
    }

    internal struct Poly1305
    {
        private uint _r0;
        private uint _r1;
        private uint _r2;
        private uint _r3;
        private uint _r4;
        private uint _h0;
        private uint _h1;
        private uint _h2;
        private uint _h3;
        private uint _h4;

        public static void Authenticate(ReadOnlySpan<byte> key, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ciphertext, Span<byte> tag)
        {
            var state = new Poly1305();
            state.Start(key);
            state.Absorb(aad);
            state.Absorb(ciphertext);

            Span<byte> lengths = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(lengths.Slice(0, 8), (ulong)aad.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(lengths.Slice(8, 8), (ulong)ciphertext.Length);
            state.Absorb(lengths);
            state.Finish(key.Slice(16, 16), tag);
        }

        private void Start(ReadOnlySpan<byte> key)
        {
            _r0 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(0, 4)) & 0x3ffffff;
            _r1 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(3, 4)) >> 2) & 0x3ffff03;
            _r2 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(6, 4)) >> 4) & 0x3ffc0ff;
            _r3 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(9, 4)) >> 6) & 0x3f03fff;
            _r4 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(12, 4)) >> 8) & 0x00fffff;
            _h0 = 0;
            _h1 = 0;
            _h2 = 0;
            _h3 = 0;
            _h4 = 0;
        }

        private void Absorb(ReadOnlySpan<byte> source)
        {
            Span<byte> padded = stackalloc byte[16];
            var offset = 0;

            while (offset < source.Length)
            {
                var count = Math.Min(16, source.Length - offset);

                if (count == 16)
                {
                    Block(source.Slice(offset, 16));
                }
                else
                {
                    padded.Clear();
                    source.Slice(offset, count).CopyTo(padded);
                    Block(padded);
                }

                offset += count;
            }
        }

        private void Block(ReadOnlySpan<byte> m)
        {
            var s1 = _r1 * 5;
            var s2 = _r2 * 5;
            var s3 = _r3 * 5;
            var s4 = _r4 * 5;

            var h0 = _h0 + (BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(0, 4)) & 0x3ffffff);
            var h1 = _h1 + ((BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(3, 4)) >> 2) & 0x3ffffff);
            var h2 = _h2 + ((BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(6, 4)) >> 4) & 0x3ffffff);
            var h3 = _h3 + ((BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(9, 4)) >> 6) & 0x3ffffff);
            var h4 = _h4 + ((BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(12, 4)) >> 8) | (1u << 24));

            var d0 = (ulong)h0 * _r0 + (ulong)h1 * s4 + (ulong)h2 * s3 + (ulong)h3 * s2 + (ulong)h4 * s1;
            var d1 = (ulong)h0 * _r1 + (ulong)h1 * _r0 + (ulong)h2 * s4 + (ulong)h3 * s3 + (ulong)h4 * s2;
            var d2 = (ulong)h0 * _r2 + (ulong)h1 * _r1 + (ulong)h2 * _r0 + (ulong)h3 * s4 + (ulong)h4 * s3;
            var d3 = (ulong)h0 * _r3 + (ulong)h1 * _r2 + (ulong)h2 * _r1 + (ulong)h3 * _r0 + (ulong)h4 * s4;
            var d4 = (ulong)h0 * _r4 + (ulong)h1 * _r3 + (ulong)h2 * _r2 + (ulong)h3 * _r1 + (ulong)h4 * _r0;

            var c = d0 >> 26;
            h0 = (uint)d0 & 0x3ffffff;
            d1 += c;
            c = d1 >> 26;
            h1 = (uint)d1 & 0x3ffffff;
            d2 += c;
            c = d2 >> 26;
            h2 = (uint)d2 & 0x3ffffff;
            d3 += c;
            c = d3 >> 26;
            h3 = (uint)d3 & 0x3ffffff;
            d4 += c;
            c = d4 >> 26;
            h4 = (uint)d4 & 0x3ffffff;
            h0 += (uint)c * 5;
            var carry = h0 >> 26;
            h0 &= 0x3ffffff;
            h1 += carry;

            _h0 = h0;
            _h1 = h1;
            _h2 = h2;
            _h3 = h3;
            _h4 = h4;
        }

        private void Finish(ReadOnlySpan<byte> pad, Span<byte> tag)
        {
            var h0 = _h0;
            var h1 = _h1;
            var h2 = _h2;
            var h3 = _h3;
            var h4 = _h4;

            var carry = h1 >> 26;
            h1 &= 0x3ffffff;
            h2 += carry;
            carry = h2 >> 26;
            h2 &= 0x3ffffff;
            h3 += carry;
            carry = h3 >> 26;
            h3 &= 0x3ffffff;
            h4 += carry;
            carry = h4 >> 26;
            h4 &= 0x3ffffff;
            h0 += carry * 5;
            carry = h0 >> 26;
            h0 &= 0x3ffffff;
            h1 += carry;

            var g0 = h0 + 5;
            carry = g0 >> 26;
            g0 &= 0x3ffffff;
            var g1 = h1 + carry;
            carry = g1 >> 26;
            g1 &= 0x3ffffff;
            var g2 = h2 + carry;
            carry = g2 >> 26;
            g2 &= 0x3ffffff;
            var g3 = h3 + carry;
            carry = g3 >> 26;
            g3 &= 0x3ffffff;
            var g4 = h4 + carry - (1u << 26);

            var mask = (g4 >> 31) - 1;
            g0 &= mask;
            g1 &= mask;
            g2 &= mask;
            g3 &= mask;
            g4 &= mask;
            mask = ~mask;
            h0 = (h0 & mask) | g0;
            h1 = (h1 & mask) | g1;
            h2 = (h2 & mask) | g2;
            h3 = (h3 & mask) | g3;
            h4 = (h4 & mask) | g4;

            h0 |= h1 << 26;
            h1 = (h1 >> 6) | (h2 << 20);
            h2 = (h2 >> 12) | (h3 << 14);
            h3 = (h3 >> 18) | (h4 << 8);

            var f = (ulong)h0 + BinaryPrimitives.ReadUInt32LittleEndian(pad.Slice(0, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(0, 4), (uint)f);
            f = (ulong)h1 + BinaryPrimitives.ReadUInt32LittleEndian(pad.Slice(4, 4)) + (f >> 32);
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(4, 4), (uint)f);
            f = (ulong)h2 + BinaryPrimitives.ReadUInt32LittleEndian(pad.Slice(8, 4)) + (f >> 32);
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(8, 4), (uint)f);
            f = (ulong)h3 + BinaryPrimitives.ReadUInt32LittleEndian(pad.Slice(12, 4)) + (f >> 32);
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(12, 4), (uint)f);

            this = default;
        }
    }
}
