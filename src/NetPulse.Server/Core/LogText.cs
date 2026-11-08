using System.Text;

namespace NetPulse.Server.Core;

public static class LogText
{
    private const int MaxLength = 128;

    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxLength));

        foreach (var character in value)
        {
            if (builder.Length >= MaxLength)
            {
                builder.Append('~');
                break;
            }

            builder.Append(IsControl(character) ? '?' : character);
        }

        return builder.ToString();
    }

    public static bool ContainsControl(string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (IsControl(character))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsControl(char character) =>
        char.IsControl(character) || character is '\u2028' or '\u2029' or '\u200E' or '\u200F' or '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E';
}
