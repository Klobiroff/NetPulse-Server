using System;
using System.Security.Cryptography;

namespace NetPulse.Common.Security
{
    public static class SecureRandom
    {
        [ThreadStatic]
        private static RandomNumberGenerator CachedGenerator;

        private static RandomNumberGenerator Generator => CachedGenerator ??= RandomNumberGenerator.Create();

        public static void Fill(byte[] destination) => Generator.GetBytes(destination);

        public static void Fill(Span<byte> destination)
        {
            var bytes = new byte[destination.Length];
            Generator.GetBytes(bytes);
            bytes.CopyTo(destination);
            Array.Clear(bytes, 0, bytes.Length);
        }

        public static byte[] Bytes(int count)
        {
            var bytes = new byte[count];
            Generator.GetBytes(bytes);
            return bytes;
        }
    }
}
