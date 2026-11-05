using System.Security.Cryptography;
using System.Text;

namespace NetPulse.Plugins.AuthSample;

public enum TokenError
{
    None,
    Malformed,
    BadSignature,
    Expired
}

public static class HmacTokens
{
    private const char Separator = '|';

    public static string Create(string secret, string userId, string nickName, DateTimeOffset expiresAt)
    {
        if (userId.Contains(Separator) || nickName.Contains(Separator))
        {
            throw new ArgumentException($"userId and nickName must not contain '{Separator}'");
        }

        var payload = Encoding.UTF8.GetBytes($"{userId}{Separator}{nickName}{Separator}{expiresAt.ToUnixTimeSeconds()}");
        return $"{ToBase64Url(payload)}.{ToBase64Url(Sign(secret, payload))}";
    }

    public static TokenError TryValidate(string secret, string? token, DateTimeOffset now, out string userId, out string nickName)
    {
        userId = string.Empty;
        nickName = string.Empty;
        var parts = token?.Split('.');

        if (parts is not { Length: 2 } || !TryFromBase64Url(parts[0], out var payload) || !TryFromBase64Url(parts[1], out var signature))
        {
            return TokenError.Malformed;
        }

        if (!CryptographicOperations.FixedTimeEquals(Sign(secret, payload), signature))
        {
            return TokenError.BadSignature;
        }

        var fields = Encoding.UTF8.GetString(payload).Split(Separator);

        if (fields.Length != 3 || !long.TryParse(fields[2], out var expiry))
        {
            return TokenError.Malformed;
        }

        if (expiry < now.ToUnixTimeSeconds())
        {
            return TokenError.Expired;
        }

        userId = fields[0];
        nickName = fields[1];
        return TokenError.None;
    }

    private static byte[] Sign(string secret, byte[] payload) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);

    private static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        bytes = new byte[padded.Length];

        if (Convert.TryFromBase64String(padded, bytes, out var written))
        {
            bytes = bytes[..written];
            return true;
        }

        bytes = Array.Empty<byte>();
        return false;
    }
}
