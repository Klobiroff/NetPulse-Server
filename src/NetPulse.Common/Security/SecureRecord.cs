using System;
using System.Security.Cryptography;

namespace NetPulse.Common.Security
{
    public static class SecureRecord
    {
        public const byte Init = 0x1F;
        public const byte Retry = 0x3F;
        public const byte Response = 0x5F;
        public const byte Data = 0x7F;
        public const byte DataPadded = 0xFF;
        public const byte Version = 1;

        public const int HeaderSize = 10;
        public const int TagSize = 16;
        public const int Overhead = HeaderSize + TagSize;
        public const int PaddedOverhead = Overhead + 2;
        public const int CookieSize = 16;
        public const int KeyIdSize = 4;
        public const int NoiseMessageSize = 48;
        public const int InitFixedSize = 7;
        public const int InitMinimumSize = 96;
        public const int ResponseSize = 1 + NoiseMessageSize;
        public const int RetrySize = 2 + CookieSize;
        public const int MaxPaddingBlock = 512;

        public static bool IsRecord(byte first) => (first & 0x1F) == 0x1F;

        public static int MaxOverhead(int paddingBlock) => paddingBlock > 0 ? PaddedOverhead + paddingBlock - 1 : Overhead;

        public static byte[] ComputeKeyId(ReadOnlySpan<byte> publicKey)
        {
            Span<byte> hash = stackalloc byte[HmacSha256.MacSize];
            HmacSha256.Hash(publicKey, hash);
            return hash.Slice(0, KeyIdSize).ToArray();
        }

        public static byte[] CreatePrologue(ReadOnlySpan<byte> keyId)
        {
            var prologue = new byte[2 + KeyIdSize];
            prologue[0] = Init;
            prologue[1] = Version;
            keyId.Slice(0, KeyIdSize).CopyTo(prologue.AsSpan(2));
            return prologue;
        }

        public static int WriteInit(byte[] buffer, int offset, ReadOnlySpan<byte> keyId, ReadOnlySpan<byte> cookie, ReadOnlySpan<byte> noiseMessage)
        {
            var length = Math.Max(InitMinimumSize, InitFixedSize + cookie.Length + NoiseMessageSize);

            if (offset + length > buffer.Length)
            {
                return -1;
            }

            buffer[offset] = Init;
            buffer[offset + 1] = Version;
            keyId.Slice(0, KeyIdSize).CopyTo(buffer.AsSpan(offset + 2));
            buffer[offset + 6] = (byte)cookie.Length;
            cookie.CopyTo(buffer.AsSpan(offset + InitFixedSize));
            noiseMessage.CopyTo(buffer.AsSpan(offset + InitFixedSize + cookie.Length));
            var used = InitFixedSize + cookie.Length + NoiseMessageSize;
            Array.Clear(buffer, offset + used, length - used);
            return length;
        }

        public static bool TryParseInit(ReadOnlySpan<byte> record, out ReadOnlySpan<byte> keyId, out ReadOnlySpan<byte> cookie, out ReadOnlySpan<byte> noiseMessage)
        {
            keyId = default;
            cookie = default;
            noiseMessage = default;

            if (record.Length < InitFixedSize + NoiseMessageSize || record[0] != Init || record[1] != Version)
            {
                return false;
            }

            int cookieLength = record[6];

            if (cookieLength != 0 && cookieLength != CookieSize)
            {
                return false;
            }

            if (record.Length < InitFixedSize + cookieLength + NoiseMessageSize)
            {
                return false;
            }

            keyId = record.Slice(2, KeyIdSize);
            cookie = record.Slice(InitFixedSize, cookieLength);
            noiseMessage = record.Slice(InitFixedSize + cookieLength, NoiseMessageSize);
            return true;
        }

        public static int WriteRetry(byte[] buffer, int offset, ReadOnlySpan<byte> cookie)
        {
            buffer[offset] = Retry;
            buffer[offset + 1] = (byte)cookie.Length;
            cookie.CopyTo(buffer.AsSpan(offset + 2));
            return 2 + cookie.Length;
        }

        public static bool TryParseRetry(ReadOnlySpan<byte> record, out ReadOnlySpan<byte> cookie)
        {
            cookie = default;

            if (record.Length < RetrySize || record[0] != Retry || record[1] != CookieSize)
            {
                return false;
            }

            cookie = record.Slice(2, CookieSize);
            return true;
        }

        public static int WriteResponse(byte[] buffer, int offset, ReadOnlySpan<byte> noiseMessage)
        {
            buffer[offset] = Response;
            noiseMessage.CopyTo(buffer.AsSpan(offset + 1));
            return ResponseSize;
        }

        public static void FillRandom(Span<byte> destination)
        {
            var bytes = new byte[destination.Length];

            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            bytes.CopyTo(destination);
            Array.Clear(bytes, 0, bytes.Length);
        }
    }
}
