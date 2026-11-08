using System;
using System.Security.Cryptography;

namespace NetPulse.Common.Security
{
    public static class HmacSha256
    {
        public const int MacSize = 32;
        public const int MaxMessageSize = 256;

        private const int BlockSize = 64;

        [ThreadStatic]
        private static SHA256 CachedSha;

        private static SHA256 Sha => CachedSha ??= SHA256.Create();

        public static void Hash(ReadOnlySpan<byte> data, Span<byte> output)
        {
            if (!Sha.TryComputeHash(data, output, out var written) || written != MacSize)
            {
                throw new CryptographicException("SHA-256 failed");
            }
        }

        public static void Compute(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message, Span<byte> output) =>
            Compute(key, message, ReadOnlySpan<byte>.Empty, output);

        public static void Compute(ReadOnlySpan<byte> key, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, Span<byte> output)
        {
            if (first.Length + second.Length > MaxMessageSize)
            {
                throw new ArgumentException("HMAC message is too long");
            }

            Span<byte> keyBlock = stackalloc byte[BlockSize];
            keyBlock.Clear();

            if (key.Length > BlockSize)
            {
                Hash(key, keyBlock);
            }
            else
            {
                key.CopyTo(keyBlock);
            }

            Span<byte> inner = stackalloc byte[BlockSize + MaxMessageSize];
            Span<byte> outer = stackalloc byte[BlockSize + MacSize];
            Span<byte> innerHash = stackalloc byte[MacSize];

            for (var i = 0; i < BlockSize; i++)
            {
                inner[i] = (byte)(keyBlock[i] ^ 0x36);
                outer[i] = (byte)(keyBlock[i] ^ 0x5c);
            }

            first.CopyTo(inner.Slice(BlockSize));
            second.CopyTo(inner.Slice(BlockSize + first.Length));
            Hash(inner.Slice(0, BlockSize + first.Length + second.Length), innerHash);
            innerHash.CopyTo(outer.Slice(BlockSize));
            Hash(outer, output);

            keyBlock.Clear();
            inner.Clear();
            outer.Clear();
            innerHash.Clear();
        }
    }

    public static class Hkdf
    {
        public const int HashSize = 32;

        public static void Extract(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> inputKeyMaterial, Span<byte> pseudoRandomKey) =>
            HmacSha256.Compute(salt, inputKeyMaterial, pseudoRandomKey);

        public static void Expand(ReadOnlySpan<byte> pseudoRandomKey, ReadOnlySpan<byte> info, Span<byte> output)
        {
            if (output.Length > 255 * HashSize || info.Length > 200)
            {
                throw new ArgumentException("HKDF output or info is too long");
            }

            Span<byte> previous = stackalloc byte[HashSize];
            Span<byte> input = stackalloc byte[HashSize + 200 + 1];
            var previousLength = 0;
            var produced = 0;
            byte counter = 1;

            while (produced < output.Length)
            {
                previous.Slice(0, previousLength).CopyTo(input);
                info.CopyTo(input.Slice(previousLength));
                input[previousLength + info.Length] = counter;
                HmacSha256.Compute(pseudoRandomKey, input.Slice(0, previousLength + info.Length + 1), previous);
                previousLength = HashSize;

                var count = Math.Min(HashSize, output.Length - produced);
                previous.Slice(0, count).CopyTo(output.Slice(produced));
                produced += count;
                counter++;
            }

            previous.Clear();
            input.Clear();
        }

        public static void Derive(ReadOnlySpan<byte> chainingKey, ReadOnlySpan<byte> inputKeyMaterial, Span<byte> first, Span<byte> second, Span<byte> third)
        {
            Span<byte> temporaryKey = stackalloc byte[HashSize];
            Span<byte> output = stackalloc byte[HashSize * 3];
            Extract(chainingKey, inputKeyMaterial, temporaryKey);
            Expand(temporaryKey, ReadOnlySpan<byte>.Empty, output.Slice(0, third.IsEmpty ? (second.IsEmpty ? HashSize : HashSize * 2) : HashSize * 3));
            output.Slice(0, HashSize).CopyTo(first);

            if (!second.IsEmpty)
            {
                output.Slice(HashSize, HashSize).CopyTo(second);
            }

            if (!third.IsEmpty)
            {
                output.Slice(HashSize * 2, HashSize).CopyTo(third);
            }

            temporaryKey.Clear();
            output.Clear();
        }
    }
}
