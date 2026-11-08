using System;

namespace NetPulse.Common.Security
{
    public enum ClientEncryptionMode
    {
        Off,
        Required
    }

    public static class SecureDisconnectReason
    {
        public const string SecureHandshakeFailed = "SecureHandshakeFailed";
        public const string SecureHandshakeTimeout = "SecureHandshakeTimeout";
        public const string RateLimited = "RateLimited";
    }

    public interface ISecureNetTransport : INetTransport
    {
        bool IsEncrypted { get; }

        void ConfigureSecurity(SecureClientSettings settings);
    }

    public static class SecurityKeyText
    {
        public static bool TryParseKey(string text, out byte[] key)
        {
            key = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                var bytes = Convert.FromBase64String(text.Trim());

                if (bytes.Length != X25519.KeySize)
                {
                    return false;
                }

                key = bytes;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public static string ToText(byte[] key) => Convert.ToBase64String(key);
    }
}
