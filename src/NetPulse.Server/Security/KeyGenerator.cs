using NetPulse.Common.Security;

namespace NetPulse.Server.Security;

public static class KeyGenerator
{
    public const string DefaultFileName = "server.key";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var path = DefaultFileName;
        var force = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    path = args[++i];
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    error.WriteLine("Usage: NetPulse.Server keygen [--out <file>] [--force]");
                    return 2;
            }
        }

        var fullPath = Path.GetFullPath(path);

        if (File.Exists(fullPath) && !force)
        {
            error.WriteLine($"Refusing to overwrite '{fullPath}'. Use --force to replace it.");
            return 1;
        }

        var privateKey = X25519.GeneratePrivateKey();
        var publicKey = X25519.GetPublicKey(privateKey);

        try
        {
            WritePrivateKey(fullPath, privateKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Could not write '{fullPath}': {ex.Message}");
            return 1;
        }
        finally
        {
            ConstantTime.Zero(privateKey);
        }

        output.WriteLine($"Private key written to {fullPath}");
        output.WriteLine("Keep it secret and out of version control. Point NetPulse:Encryption:PrivateKeyFile at it.");
        output.WriteLine($"Public key (put it in the client settings): {SecurityKeyText.ToText(publicKey)}");
        return 0;
    }

    private static void WritePrivateKey(string path, byte[] privateKey)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var text = SecurityKeyText.ToText(privateKey) + Environment.NewLine;
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }
}
