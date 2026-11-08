using NetPulse.Common;
using NetPulse.Common.Security;
using NetPulse.TestClient;

var options = BotOptions.Parse(args);

if (options.MakeToken)
{
    if (string.IsNullOrEmpty(options.Secret))
    {
        Console.Error.WriteLine("--make-token requires --secret");
        return 1;
    }

    var expires = DateTimeOffset.UtcNow.AddMinutes(options.TtlMinutes);
    Console.WriteLine(NetPulse.Plugins.AuthSample.HmacTokens.Create(options.Secret, options.UserId, options.NickName, expires));
    return 0;
}

if (options.ShowHelp)
{
    Console.WriteLine("NetPulse.TestClient --host 127.0.0.1 --port 5055 --bots 2 --room test --plugin NetPulse.Plugins.Sample --seconds 6 [--app-id ID] [--key ConnectionKey] [--token T] [--encrypt --server-public-key BASE64 [--psk BASE64] [--padding N]] | --make-token --secret S [--user U] [--nick N] [--ttl-minutes 60]");
    return 0;
}

var bots = new List<BotClient>();
var failures = new List<string>();

try
{
    for (var i = 0; i < options.Bots; i++)
    {
        var bot = new BotClient($"bot{i + 1}", options.BuildSecurity());
        bots.Add(bot);
        await bot.ConnectAsync(options.Host, options.Port, options.ConnectionKey);
        var auth = await bot.AuthenticateAsync(appId: options.AppId, authToken: options.AuthToken);
        Check(auth.ReturnCode == ReturnCode.Ok, $"{bot.Name}: authenticate failed ({auth.ReturnCode} {auth.DebugMessage})");
        var join = await bot.JoinOrCreateRoomAsync(options.Room, new RoomOptions { MaxPlayers = (byte)Math.Max(options.Bots, 2), Plugin = options.Plugin });
        Check(join.ReturnCode == ReturnCode.Ok, $"{bot.Name}: join failed ({join.ReturnCode} {join.DebugMessage})");
        Console.WriteLine($"{bot.Name} joined '{options.Room}' as actor {bot.ActorId}, master {bot.MasterClientId}, encrypted {bot.IsEncrypted}");
    }

    var end = DateTime.UtcNow.AddSeconds(options.Seconds);
    var round = 0;

    while (DateTime.UtcNow < end && failures.Count == 0)
    {
        round++;

        foreach (var bot in bots)
        {
            await bot.RaiseEventAsync(20, $"hello {round} from {bot.Name}", ReceiverGroup.Others);
            await bot.RaiseEventAsync(SampleEvents.AddScore, 5);
            bot.SendPing();
        }

        await Task.Delay(500);
    }

    var rejected = await bots[0].RaiseEventAsync(SampleEvents.Forbidden, "x");
    Check(rejected.ReturnCode == ReturnCode.PluginReportedError, "plugin did not reject event 99");

    foreach (var bot in bots)
    {
        var chats = bot.CountEvents(20);
        var ticks = bot.CountEvents(SampleEvents.Tick);
        var score = bot.RoomProperties.Get<int>($"score_{bot.ActorId}");
        Console.WriteLine($"{bot.Name}: chats={chats} ticks={ticks} score={score} serverTime={bot.ServerTimeMs}ms");
        Check(bots.Count < 2 || chats > 0, $"{bot.Name}: received no events from other bots");
        Check(options.Plugin.Length == 0 || ticks > 0, $"{bot.Name}: received no timer ticks from plugin");
        Check(options.Plugin.Length == 0 || score > 0, $"{bot.Name}: authoritative score was not updated");
    }
}
catch (Exception ex)
{
    failures.Add(ex.Message);
}
finally
{
    foreach (var bot in bots)
    {
        bot.Dispose();
    }
}

foreach (var failure in failures)
{
    Console.Error.WriteLine("FAIL: " + failure);
}

Console.WriteLine(failures.Count == 0 ? "OK" : "FAILED");
return failures.Count == 0 ? 0 : 1;

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

public static class SampleEvents
{
    public const byte AddScore = 10;
    public const byte Tick = 50;
    public const byte Forbidden = 99;
}

public sealed class BotOptions
{
    public string Host { get; private set; } = "127.0.0.1";

    public int Port { get; private set; } = Protocol.DefaultPort;

    public int Bots { get; private set; } = 2;

    public string Room { get; private set; } = "test";

    public string Plugin { get; private set; } = "NetPulse.Plugins.Sample";

    public int Seconds { get; private set; } = 6;

    public string? AppId { get; private set; }

    public string? AuthToken { get; private set; }

    public string ConnectionKey { get; private set; } = Protocol.DefaultConnectionKey;

    public bool Encrypt { get; private set; }

    public string ServerPublicKey { get; private set; } = string.Empty;

    public string PreSharedKey { get; private set; } = string.Empty;

    public int Padding { get; private set; }

    public bool ShowHelp { get; private set; }

    public bool MakeToken { get; private set; }

    public string Secret { get; private set; } = string.Empty;

    public string UserId { get; private set; } = "user1";

    public string NickName { get; private set; } = "Player";

    public int TtlMinutes { get; private set; } = 60;

    public SecureClientSettings? BuildSecurity()
    {
        if (!Encrypt)
        {
            return null;
        }

        if (!SecurityKeyText.TryParseKey(ServerPublicKey, out var serverKey))
        {
            throw new ArgumentException("--encrypt requires --server-public-key with a Base64 32-byte key");
        }

        var settings = new SecureClientSettings { ServerPublicKeys = new List<byte[]> { serverKey } };
        settings.Session.PaddingBlock = Padding;

        if (PreSharedKey.Length > 0)
        {
            settings.PreSharedKey = SecurityKeyText.TryParseKey(PreSharedKey, out var psk) ? psk : throw new ArgumentException("--psk must be a Base64 32-byte key");
        }

        return settings;
    }

    public static BotOptions Parse(string[] args)
    {
        var result = new BotOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : string.Empty;

            switch (args[i])
            {
                case "--host":
                    result.Host = value;
                    i++;
                    break;
                case "--port":
                    result.Port = int.Parse(value);
                    i++;
                    break;
                case "--bots":
                    result.Bots = int.Parse(value);
                    i++;
                    break;
                case "--room":
                    result.Room = value;
                    i++;
                    break;
                case "--plugin":
                    result.Plugin = value;
                    i++;
                    break;
                case "--seconds":
                    result.Seconds = int.Parse(value);
                    i++;
                    break;
                case "--token":
                    result.AuthToken = value;
                    i++;
                    break;
                case "--app-id":
                    result.AppId = value;
                    i++;
                    break;
                case "--key":
                    result.ConnectionKey = value;
                    i++;
                    break;
                case "--encrypt":
                    result.Encrypt = true;
                    break;
                case "--server-public-key":
                    result.ServerPublicKey = value;
                    i++;
                    break;
                case "--psk":
                    result.PreSharedKey = value;
                    i++;
                    break;
                case "--padding":
                    result.Padding = int.Parse(value);
                    i++;
                    break;
                case "--make-token":
                    result.MakeToken = true;
                    break;
                case "--secret":
                    result.Secret = value;
                    i++;
                    break;
                case "--user":
                    result.UserId = value;
                    i++;
                    break;
                case "--nick":
                    result.NickName = value;
                    i++;
                    break;
                case "--ttl-minutes":
                    result.TtlMinutes = int.Parse(value);
                    i++;
                    break;
                case "--help":
                case "-h":
                    result.ShowHelp = true;
                    break;
            }
        }

        return result;
    }
}
