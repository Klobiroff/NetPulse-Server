using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NetPulse.Common;
using NetPulse.Common.Security;
using NetPulse.Plugins;
using NetPulse.Server.Core;
using NetPulse.Server.Networking;
using NetPulse.TestClient;

namespace NetPulse.Server.Tests;

public abstract class SecureServerTestBase : ServerTestBase, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netpulse-tests-" + Guid.NewGuid().ToString("N"));

    protected SecureServerTestBase()
    {
        Directory.CreateDirectory(_directory);
        PrivateKey = X25519.GeneratePrivateKey();
        PublicKey = X25519.GetPublicKey(PrivateKey);
        KeyFile = Path.Combine(_directory, "server.key");
        File.WriteAllText(KeyFile, SecurityKeyText.ToText(PrivateKey));
    }

    protected byte[] PrivateKey { get; }

    protected byte[] PublicKey { get; }

    protected string KeyFile { get; }

    protected abstract string Mode { get; }

    protected virtual Dictionary<string, string?> Extra => new();

    protected NetPulseServer Server => Services.GetRequiredService<NetPulseServer>();

    protected SecurityCounters ServerCounters => Services.GetRequiredService<ServerMetrics>().Security;

    protected override Dictionary<string, string?> Settings
    {
        get
        {
            var settings = new Dictionary<string, string?>
            {
                ["NetPulse:Encryption:Mode"] = Mode,
                ["NetPulse:Encryption:PrivateKeyFile"] = KeyFile
            };

            foreach (var pair in Extra)
            {
                settings[pair.Key] = pair.Value;
            }

            return settings;
        }
    }

    protected SecureClientSettings ClientSettings(Action<SecureClientSettings>? configure = null)
    {
        var settings = new SecureClientSettings { ServerPublicKeys = new List<byte[]> { PublicKey }, HandshakeTimeoutMs = 4000 };
        configure?.Invoke(settings);
        return settings;
    }

    protected static void SendRawEvent(BotClient client, byte code, object data) =>
        client.Send(new OperationRequest
        {
            OpCode = OpCode.RaiseEvent,
            RequestId = 0,
            Parameters = new RaiseEventRequest { EventCode = code, Data = data, Receivers = ReceiverGroup.Others }.ToParameters()
        });

    protected static bool Contains(IEnumerable<byte[]> datagrams, string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        return datagrams.Any(d => d.AsSpan().IndexOf(needle) >= 0);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }

    protected static async Task<SecureClientCore?> AttackerHandshakeAsync(UdpProxy proxy, IPEndPoint client, byte[] serverPublic, TimeSpan timeout)
    {
        var settings = new SecureClientSettings { ServerPublicKeys = new List<byte[]> { serverPublic } };
        var core = new SecureClientCore(settings, new SecurityCounters(), () => MonotonicClock.NowMs);
        var garbage = new byte[40];
        garbage[0] = 5;
        core.Stash(garbage, 0, garbage.Length);
        var baseline = proxy.Capture.Count;
        var consumed = baseline;
        var deadline = DateTime.UtcNow + timeout;
        var lastSend = DateTime.MinValue;
        var buffer = new byte[256];

        while (DateTime.UtcNow < deadline)
        {
            if (DateTime.UtcNow - lastSend > TimeSpan.FromMilliseconds(250))
            {
                var length = core.BuildInit(buffer, 0);
                await proxy.SendToServerAsSameEndpointAsync(client, buffer.AsSpan(0, length).ToArray());
                lastSend = DateTime.UtcNow;
            }

            var captured = proxy.Capture.ToArray();

            for (; consumed < captured.Length; consumed++)
            {
                var (direction, data) = captured[consumed];

                if (direction != Direction.ServerToClient || data.Length == 0 || (data[0] != SecureRecord.Retry && data[0] != SecureRecord.Response))
                {
                    continue;
                }

                var reply = new byte[2048];
                var input = new byte[2048];
                data.CopyTo(input, 0);
                var result = core.ProcessInbound(input, data.Length, reply);

                if (result.Kind == SecureClientInboundKind.Reply)
                {
                    await proxy.SendToServerAsSameEndpointAsync(client, reply.AsSpan(0, result.Length).ToArray());
                }

                if (core.State == SecureClientState.Established)
                {
                    return core;
                }
            }

            await Task.Delay(20);
        }

        return null;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class X25519CounterCollection
{
    public const string Name = "X25519 operation counter";
}

[Collection(X25519CounterCollection.Name)]
public sealed class RequiredModeTests : SecureServerTestBase
{
    private const string AppId = "SecretAppId-4242";
    private const string ConnectionKey = "SuperSecretConnectionKey";

    protected override string Mode => "Required";

    protected override Dictionary<string, string?> Extra => new()
    {
        ["NetPulse:ConnectionKey"] = ConnectionKey,
        ["NetPulse:AppId"] = AppId,
        ["NetPulse:Encryption:ReplaceIdleMs"] = "60000"
    };

    private Task<BotClient> ConnectSecureAsync(string name, int? port = null, SecureClientSettings? security = null) =>
        ConnectAsync(name, appId: AppId, security: security ?? ClientSettings(), port: port, connectionKey: ConnectionKey, authToken: "tok-SECRET-abcdef");

    [Fact]
    public async Task EncryptedClientsExchangeEventsAndPeersAreMarkedEncrypted()
    {
        var a = await ConnectSecureAsync("a");
        var b = await ConnectSecureAsync("b");
        await JoinAsync(a, "room");
        await JoinAsync(b, "room");
        await a.RaiseEventAsync(20, "hello", ReceiverGroup.Others);
        await b.WaitForEventAsync(20);

        Assert.True(a.IsEncrypted);
        Assert.All(Server.Peers, peer => Assert.True(peer.IsEncrypted));
        Assert.Equal(2, ServerCounters.HandshakesSucceeded);
        Assert.Equal(1, a.SecurityCounters.HandshakesSucceeded);
    }

    [Fact]
    public async Task PlaintextClientCannotConnectAndIsCounted()
    {
        using var plain = new BotClient("plain");
        await Assert.ThrowsAnyAsync<Exception>(() => plain.ConnectAsync("127.0.0.1", Port, ConnectionKey, TimeSpan.FromSeconds(2)));
        Assert.True(ServerCounters.PlaintextRejected > 0);
        Assert.Equal(0, Server.PeerCount);
    }

    [Fact]
    public async Task WrongServerKeyReportsHandshakeTimeoutAndServerCountsTheFailure()
    {
        var wrong = ClientSettings(settings =>
        {
            settings.ServerPublicKeys = new List<byte[]> { X25519.GetPublicKey(X25519.GeneratePrivateKey()) };
            settings.HandshakeTimeoutMs = 1200;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectSecureAsync("wrong", security: wrong));
        Assert.Contains(SecureDisconnectReason.SecureHandshakeTimeout, error.Message);
        Assert.True(ServerCounters.HandshakesFailed >= 1);
        Assert.Equal(0, Server.PeerCount);
    }

    [Fact]
    public async Task CapturedTrafficContainsNoPlaintextSecrets()
    {
        using var proxy = new UdpProxy(Port);
        var a = await ConnectSecureAsync("AlicePlayerNick", proxy.Port);
        var b = await ConnectSecureAsync("BobPlayerNick", proxy.Port);
        await JoinAsync(a, "secretroom");
        await JoinAsync(b, "secretroom");
        await a.RaiseEventAsync(20, "confidential-chat-message", ReceiverGroup.Others);
        await b.WaitForEventAsync(20);

        var all = proxy.Capture.Select(c => c.Data).ToArray();
        Assert.NotEmpty(all);

        foreach (var secret in new[] { AppId, ConnectionKey, "tok-SECRET-abcdef", "AlicePlayerNick", "secretroom", "confidential-chat-message", "NetPulse" })
        {
            Assert.False(Contains(all, secret), $"{secret} appears in captured datagrams");
        }

        Assert.All(all, datagram => Assert.True(SecureRecord.IsRecord(datagram[0]), "a datagram without the secure record marker was sent"));
    }

    [Fact]
    public async Task HandshakeRepliesAreShorterThanTheirRequests()
    {
        using var proxy = new UdpProxy(Port);
        await ConnectSecureAsync("a", proxy.Port);
        var all = proxy.Capture.ToArray();
        var inits = all.Where(c => c.Direction == Direction.ClientToServer && c.Data[0] == SecureRecord.Init).Select(c => c.Data.Length).ToArray();
        var replies = all.Where(c => c.Direction == Direction.ServerToClient && (c.Data[0] == SecureRecord.Retry || c.Data[0] == SecureRecord.Response)).Select(c => c.Data.Length).ToArray();
        Assert.True(inits.Length >= 2);
        Assert.True(replies.Length >= 2);
        Assert.True(replies.Max() < inits.Min());
    }

    [Fact]
    public async Task NoDiffieHellmanHappensBeforeACookieIsEchoed()
    {
        var settings = ClientSettings();
        using var core = new SecureClientCore(settings, new SecurityCounters(), () => MonotonicClock.NowMs);
        var init = new byte[256];
        var length = core.BuildInit(init, 0);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var before = X25519.OperationCount;
        await udp.SendAsync(init, length, new IPEndPoint(IPAddress.Loopback, Port));
        var reply = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(SecureRecord.Retry, reply.Buffer[0]);
        Assert.True(reply.Buffer.Length < length);
        Assert.Equal(before, X25519.OperationCount);
        Assert.Equal(0, Server.Secure!.SessionCount);
    }

    [Fact]
    public async Task ReplayedDatagramsAreIgnoredAndCounted()
    {
        using var proxy = new UdpProxy(Port);
        var a = await ConnectSecureAsync("a", proxy.Port);
        var b = await ConnectSecureAsync("b", proxy.Port);
        await JoinAsync(a, "room");
        await JoinAsync(b, "room");
        await Task.Delay(200);

        var before = proxy.Capture.Count;
        await a.RaiseEventAsync(21, "unique-payload", ReceiverGroup.Others);
        await b.WaitForEventAsync(21);
        var replays = ServerCounters.Replays;
        var sent = proxy.Capture.ToArray().Skip(before).Where(c => c.Direction == Direction.ClientToServer && c.Data[0] == SecureRecord.Data).Select(c => c.Data).ToArray();
        Assert.NotEmpty(sent);
        var clientEndpoint = proxy.FirstClient!;

        foreach (var datagram in sent)
        {
            await proxy.SendToServerAsSameEndpointAsync(clientEndpoint, datagram);
        }

        await Task.Delay(300);
        Assert.Equal(1, b.CountEvents(21));
        Assert.True(ServerCounters.Replays - replays >= 1);
    }

    [Fact]
    public async Task EveryModifiedByteOfAHeldDatagramIsRejectedWithoutConsumingTheOriginal()
    {
        using var proxy = new UdpProxy(Port);
        var a = await ConnectSecureAsync("a", proxy.Port);
        var b = await ConnectSecureAsync("b", proxy.Port);
        await JoinAsync(a, "room");
        await JoinAsync(b, "room");
        await Task.Delay(200);

        var held = new List<byte[]>();
        var holding = true;
        proxy.Filter = (direction, data) =>
        {
            if (holding && direction == Direction.ClientToServer && data[0] == SecureRecord.Data)
            {
                lock (held)
                {
                    held.Add(data);
                }

                return Array.Empty<byte[]>();
            }

            return new[] { data };
        };

        SendRawEvent(a, 22, "held-event");
        await Eventually(() =>
        {
            lock (held)
            {
                return held.Count > 0;
            }
        });

        byte[] sample;

        lock (held)
        {
            sample = held[0];
        }

        var client = proxy.FirstClient!;
        var rejectedBefore = ServerCounters.DecryptFailures + ServerCounters.PlaintextRejected;
        var replaysBefore = ServerCounters.Replays;
        var variants = 0;

        for (var i = 0; i < sample.Length; i++)
        {
            foreach (var mask in new byte[] { 0x01, 0x80 })
            {
                var mutated = (byte[])sample.Clone();
                mutated[i] ^= mask;
                await proxy.SendToServerAsSameEndpointAsync(client, mutated);
                variants++;
            }
        }

        await Eventually(() => ServerCounters.DecryptFailures + ServerCounters.PlaintextRejected - rejectedBefore >= variants);
        Assert.Equal(replaysBefore, ServerCounters.Replays);
        Assert.Equal(0, b.CountEvents(22));

        holding = false;
        byte[][] snapshot;

        lock (held)
        {
            snapshot = held.ToArray();
        }

        foreach (var datagram in snapshot)
        {
            await proxy.SendToServerAsSameEndpointAsync(client, datagram);
        }

        await b.WaitForEventAsync(22);
        Assert.Equal(1, b.CountEvents(22));
    }

    [Fact]
    public async Task TamperedTrafficIsDroppedWhileReliableDeliveryRecovers()
    {
        using var proxy = new UdpProxy(Port);
        var a = await ConnectSecureAsync("a", proxy.Port);
        var counter = 0;
        proxy.Filter = (direction, data) =>
        {
            if (direction == Direction.ClientToServer && data[0] == SecureRecord.Data && Interlocked.Increment(ref counter) % 3 == 0)
            {
                var mutated = (byte[])data.Clone();
                mutated[mutated.Length / 2] ^= 0x10;
                return new[] { mutated };
            }

            return new[] { data };
        };

        for (var i = 0; i < 15; i++)
        {
            var response = await a.JoinLobbyAsync();
            Assert.Equal(ReturnCode.Ok, response.ReturnCode);
        }

        Assert.True(ServerCounters.DecryptFailures > 0);
    }

    [Fact]
    public async Task ActiveSessionIsNotReplacedAndGivesTheAttackerNoPeer()
    {
        using var proxy = new UdpProxy(Port);
        var a = await ConnectSecureAsync("victim", proxy.Port);
        await JoinAsync(a, "room");
        var limited = ServerCounters.HandshakesRateLimited;

        var attacker = await AttackerHandshakeAsync(proxy, proxy.FirstClient!, PublicKey, TimeSpan.FromSeconds(2));

        Assert.Null(attacker);
        Assert.True(ServerCounters.HandshakesRateLimited > limited);
        var response = await a.JoinLobbyAsync();
        Assert.Equal(ReturnCode.Ok, response.ReturnCode);
        Assert.Single(Server.Peers);
        Assert.True(Server.Peers.Single().Authenticated);
    }
}

public sealed class SessionReplacementTests : SecureServerTestBase
{
    protected override string Mode => "Required";

    protected override Dictionary<string, string?> Extra => new()
    {
        ["NetPulse:Encryption:ReplaceIdleMs"] = "300"
    };

    [Fact]
    public async Task IdleSessionReplacementDisconnectsTheOldPeerFirstAndTheNewSessionHasNoPeer()
    {
        using var proxy = new UdpProxy(Port);
        var victim = await ConnectAsync("victim", security: ClientSettings(), port: proxy.Port);
        await JoinAsync(victim, "room");
        Assert.Single(Server.Peers);

        var endpoint = proxy.FirstClient!;
        var upstream = proxy.UpstreamEndpointOf(endpoint)!;
        var key = SecureEndpoint.From(upstream.Address, upstream.Port);
        var attacker = await AttackerHandshakeAsync(proxy, endpoint, PublicKey, TimeSpan.FromSeconds(6));

        Assert.NotNull(attacker);
        await Eventually(() => Server.PeerCount == 0);
        Assert.DoesNotContain(Server.Peers, peer => peer.Authenticated);
        Assert.True(Server.Secure!.HasActiveSession(key));
        Assert.NotNull(Server.Secure.Bind(key, "fresh-peer"));
    }
}

public sealed class BindingTests : SecureServerTestBase
{
    private readonly CapturingAuth _auth = new();

    protected override string Mode => "Required";

    protected override void RegisterPlugins(NetPulse.Server.Plugins.PluginManager plugins) => plugins.AuthPlugin = _auth;

    [Fact]
    public async Task AuthPluginSeesEncryptionFlagAndChannelBindingOfTheHandshake()
    {
        var bot = await ConnectAsync("bound", security: ClientSettings());
        var request = _auth.Last!;
        Assert.True(request.IsEncrypted);
        Assert.Equal(bot.HandshakeHash, request.ChannelBinding);
        Assert.Equal(32, request.ChannelBinding!.Length);
        var peer = Server.Peers.Single();
        Assert.Equal(bot.HandshakeHash, peer.ChannelBinding);
    }

    private sealed class CapturingAuth : IAuthPlugin
    {
        public AuthRequest? Last { get; private set; }

        public Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(AuthResult.Ok());
        }
    }
}

public sealed class PreSharedKeyTests : SecureServerTestBase
{
    private readonly byte[] _psk = Enumerable.Range(10, 32).Select(i => (byte)i).ToArray();

    protected override string Mode => "Required";

    protected override Dictionary<string, string?> Extra => new()
    {
        ["NetPulse:Encryption:PreSharedKey"] = SecurityKeyText.ToText(_psk)
    };

    [Fact]
    public async Task MatchingPreSharedKeyConnects()
    {
        var bot = await ConnectAsync("psk", security: ClientSettings(settings => settings.PreSharedKey = _psk));
        Assert.True(bot.IsEncrypted);
    }

    [Fact]
    public async Task MissingOrWrongPreSharedKeyIsRejectedAsTimeout()
    {
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync("none", security: ClientSettings(settings => settings.HandshakeTimeoutMs = 1200)));
        Assert.Contains(SecureDisconnectReason.SecureHandshakeTimeout, missing.Message);

        var wrong = Enumerable.Range(50, 32).Select(i => (byte)i).ToArray();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync("wrong", security: ClientSettings(settings =>
        {
            settings.PreSharedKey = wrong;
            settings.HandshakeTimeoutMs = 1200;
        })));
        Assert.Contains(SecureDisconnectReason.SecureHandshakeTimeout, error.Message);
        Assert.True(ServerCounters.HandshakesFailed >= 2);
    }
}

public sealed class PaddingTests : SecureServerTestBase
{
    protected override string Mode => "Required";

    protected override Dictionary<string, string?> Extra => new()
    {
        ["NetPulse:Encryption:PaddingBlock"] = "64"
    };

    [Fact]
    public async Task DataRecordsAreMultiplesOfThePaddingBlockInBothDirections()
    {
        using var proxy = new UdpProxy(Port);
        var settings = ClientSettings(s => s.Session.PaddingBlock = 64);
        var a = await ConnectAsync("a", security: settings, port: proxy.Port);
        await JoinAsync(a, "room");
        await a.RaiseEventAsync(20, new string('x', 150), ReceiverGroup.All);
        await Task.Delay(300);

        var records = proxy.Capture.Select(c => c.Data).Where(d => d[0] is SecureRecord.Data or SecureRecord.DataPadded).ToArray();
        Assert.True(records.Length > 5);
        Assert.All(records, record =>
        {
            Assert.Equal(SecureRecord.DataPadded, record[0]);
            Assert.Equal(0, record.Length % 64);
        });
    }
}

public sealed class RekeyUnderLossTests : SecureServerTestBase
{
    protected override string Mode => "Required";

    protected override Dictionary<string, string?> Extra => new()
    {
        ["NetPulse:Encryption:RekeyAfterPackets"] = "25"
    };

    [Fact]
    public async Task ReliableTrafficSurvivesRekeysWhileDatagramsAreDropped()
    {
        using var proxy = new UdpProxy(Port);
        var a = await ConnectAsync("a", security: ClientSettings(s => s.Session.RekeyAfterPackets = 25), port: proxy.Port);
        var random = new Random(11);
        var dropping = true;
        proxy.Filter = (_, data) => dropping && data[0] == SecureRecord.Data && random.NextDouble() < 0.15 ? Array.Empty<byte[]>() : new[] { data };

        for (var i = 0; i < 40; i++)
        {
            var response = await a.JoinLobbyAsync();
            Assert.Equal(ReturnCode.Ok, response.ReturnCode);
        }

        dropping = false;
        Assert.True(ServerCounters.Rekeys > 0);
        Assert.True(a.SecurityCounters.Rekeys > 0);
    }
}

public sealed class OptionalModeTests : SecureServerTestBase
{
    protected override string Mode => "Optional";

    [Fact]
    public async Task EncryptedAndPlainClientsShareARoom()
    {
        var secure = await ConnectAsync("secure", security: ClientSettings());
        var plain = await ConnectAsync("plain");
        await JoinAsync(secure, "room");
        await JoinAsync(plain, "room");
        await secure.RaiseEventAsync(20, "to-plain", ReceiverGroup.Others);
        await plain.WaitForEventAsync(20);
        await plain.RaiseEventAsync(21, "to-secure", ReceiverGroup.Others);
        await secure.WaitForEventAsync(21);

        var peers = Server.Peers.ToDictionary(p => p.UserId);
        Assert.True(peers["secure"].IsEncrypted);
        Assert.False(peers["plain"].IsEncrypted);
        Assert.Null(peers["plain"].ChannelBinding);
    }

    [Fact]
    public async Task PlaintextFromAnEncryptedEndpointIsDropped()
    {
        using var proxy = new UdpProxy(Port);
        var secure = await ConnectAsync("secure", security: ClientSettings(), port: proxy.Port);
        var before = ServerCounters.PlaintextRejected;
        await proxy.SendToServerAsSameEndpointAsync(proxy.FirstClient!, new byte[] { 3, 0, 0, 0 });
        await Eventually(() => ServerCounters.PlaintextRejected > before);
        var response = await secure.JoinLobbyAsync();
        Assert.Equal(ReturnCode.Ok, response.ReturnCode);
    }
}

public sealed class DisabledModeTests : SecureServerTestBase
{
    protected override string Mode => "Disabled";

    protected override Dictionary<string, string?> Extra => new()
    {
        ["NetPulse:ConnectionKey"] = "PlainConnectionKey"
    };

    [Fact]
    public async Task NoLayerIsInstalledAndPlainClientsWorkWithUnchangedWireFormat()
    {
        Assert.False(Server.EncryptionEnabled);
        Assert.Null(Server.Secure);

        using var proxy = new UdpProxy(Port);
        var bot = await ConnectAsync("plain", port: proxy.Port, connectionKey: "PlainConnectionKey", authToken: "tok-visible");
        await JoinAsync(bot, "room");
        Assert.False(bot.IsEncrypted);
        Assert.False(Server.Peers.Single().IsEncrypted);
        var all = proxy.Capture.Select(c => c.Data).ToArray();
        Assert.True(Contains(all, "PlainConnectionKey"));
        Assert.True(Contains(all, "tok-visible"));
    }

    [Fact]
    public async Task EncryptingClientCannotConnectToAServerWithoutEncryption()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync("secure", security: ClientSettings(s => s.HandshakeTimeoutMs = 1200)));
        Assert.Contains(SecureDisconnectReason.SecureHandshakeTimeout, error.Message);
    }
}
