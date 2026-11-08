using System.Net;
using NetPulse.Common.Security;

namespace NetPulse.Common.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class X25519CounterCollection
{
    public const string Name = "X25519 operation counter";
}

[Collection(X25519CounterCollection.Name)]
public sealed class SecureCoreTests
{
    private static readonly byte[] ConnectRequest = Enumerable.Range(0, 60).Select(i => (byte)(i + 5)).ToArray();

    private sealed class Rig
    {
        private readonly byte[] _reply = new byte[2048];

        public Rig(Action<SecureServerSettings> server = null, Action<SecureClientSettings> client = null, byte[] serverKey = null)
        {
            ServerKey = serverKey ?? X25519.GeneratePrivateKey();
            ServerPublic = X25519.GetPublicKey(ServerKey);
            var serverSettings = new SecureServerSettings { PrivateKeys = new List<byte[]> { ServerKey } };
            server?.Invoke(serverSettings);
            var clientSettings = new SecureClientSettings { ServerPublicKeys = new List<byte[]> { ServerPublic }, HandshakeTimeoutMs = 5000 };
            client?.Invoke(clientSettings);
            ServerCounters = new SecurityCounters();
            ClientCounters = new SecurityCounters();
            Server = new SecureServerCore(serverSettings, ServerCounters, () => Now);
            Client = new SecureClientCore(clientSettings, ClientCounters, () => Now);
            Endpoint = SecureEndpoint.From(IPAddress.Parse("203.0.113.7"), 40000);
        }

        public long Now { get; set; } = 1000;

        public byte[] ServerKey { get; }

        public byte[] ServerPublic { get; }

        public SecurityCounters ServerCounters { get; }

        public SecurityCounters ClientCounters { get; }

        public SecureServerCore Server { get; }

        public SecureClientCore Client { get; }

        public SecureEndpoint Endpoint { get; }

        public List<(int Request, int Reply)> Exchanges { get; } = new();

        public byte[] FirstData { get; private set; } = Array.Empty<byte>();

        public SecureInboundResult ServerReceive(byte[] record, SecureEndpoint? endpoint = null)
        {
            var buffer = new byte[2048];
            record.CopyTo(buffer, 0);
            var result = Server.ProcessInbound(endpoint ?? Endpoint, buffer, record.Length, _reply);

            if (result.Kind == SecureInboundKind.Reply)
            {
                Exchanges.Add((record.Length, result.Length));
            }

            return result;
        }

        public byte[] ServerOutput(SecureInboundResult result) => _reply.AsSpan(0, result.Length).ToArray();

        public byte[] ServerPlain(byte[] record, SecureEndpoint? endpoint = null)
        {
            var buffer = new byte[2048];
            record.CopyTo(buffer, 0);
            var result = Server.ProcessInbound(endpoint ?? Endpoint, buffer, record.Length, _reply);
            return result.Kind == SecureInboundKind.Decrypted ? buffer.AsSpan(0, result.Length).ToArray() : Array.Empty<byte>();
        }

        public byte[] ClientInit()
        {
            var buffer = new byte[256];
            var length = Client.BuildInit(buffer, 0);
            Assert.True(length > 0);
            return buffer.AsSpan(0, length).ToArray();
        }

        public SecureClientInboundResult ClientReceive(byte[] record, out byte[] output)
        {
            var buffer = new byte[2048];
            var reply = new byte[2048];
            record.CopyTo(buffer, 0);
            var result = Client.ProcessInbound(buffer, record.Length, reply);

            output = result.Kind switch
            {
                SecureClientInboundKind.Reply => reply.AsSpan(0, result.Length).ToArray(),
                SecureClientInboundKind.Decrypted => buffer.AsSpan(0, result.Length).ToArray(),
                _ => Array.Empty<byte>()
            };

            return result;
        }

        public byte[] Seal(byte[] plaintext, SecureSession session)
        {
            var buffer = new byte[plaintext.Length + 64];
            plaintext.CopyTo(buffer, 0);
            var length = session.Protect(buffer, 0, plaintext.Length, Now);
            return buffer.AsSpan(0, length).ToArray();
        }

        public bool Handshake(byte[] connectRequest)
        {
            Client.Stash(connectRequest, 0, connectRequest.Length);
            var next = ClientInit();

            for (var step = 0; step < 6; step++)
            {
                var result = ServerReceive(next);

                if (result.Kind != SecureInboundKind.Reply)
                {
                    next = ClientInit();
                    continue;
                }

                var response = ClientReceive(ServerOutput(result), out var output);

                if (Client.State == SecureClientState.Established)
                {
                    FirstData = output;
                    return true;
                }

                if (response.Kind != SecureClientInboundKind.Reply)
                {
                    return false;
                }

                next = output;
            }

            return false;
        }
    }

    [Fact]
    public void HandshakeWithCookieEstablishesAndActivatesOnFirstData()
    {
        var rig = new Rig();
        Assert.True(rig.Handshake(ConnectRequest));
        Assert.Equal(2, rig.Exchanges.Count);
        Assert.Equal(SecureRecord.RetrySize, rig.Exchanges[0].Reply);
        Assert.Equal(SecureRecord.ResponseSize, rig.Exchanges[1].Reply);
        Assert.False(rig.Server.HasActiveSession(rig.Endpoint));
        Assert.Equal(0, rig.ServerCounters.HandshakesSucceeded);

        Assert.Equal(ConnectRequest, rig.ServerPlain(rig.FirstData));
        Assert.True(rig.Server.HasActiveSession(rig.Endpoint));
        Assert.Equal(1, rig.ServerCounters.HandshakesSucceeded);
        Assert.Equal(1, rig.ClientCounters.HandshakesSucceeded);
        Assert.Equal(rig.Client.Session!.HandshakeHash, rig.Server.Bind(rig.Endpoint, "peer")!.HandshakeHash);
    }

    [Fact]
    public void ServerAndClientExchangeDataAfterActivation()
    {
        var rig = new Rig();
        Assert.True(rig.Handshake(ConnectRequest));
        rig.ServerPlain(rig.FirstData);

        var toServer = new byte[100];
        var buffer = new byte[200];
        toServer.CopyTo(buffer, 0);
        var length = rig.Client.Protect(buffer, 0, toServer.Length);
        Assert.Equal(126, length);
        Assert.Equal(toServer, rig.ServerPlain(buffer.AsSpan(0, length).ToArray()));

        var outbound = new byte[200];
        new byte[50].CopyTo(outbound, 0);
        var written = rig.Server.ProtectOutbound(rig.Endpoint, outbound, 0, 50);
        Assert.Equal(76, written);
        var result = rig.ClientReceive(outbound.AsSpan(0, written).ToArray(), out var plain);
        Assert.Equal(SecureClientInboundKind.Decrypted, result.Kind);
        Assert.Equal(50, plain.Length);
    }

    [Fact]
    public void RepliesAreNeverLongerThanTheRequest()
    {
        var rig = new Rig();
        Assert.True(rig.Handshake(ConnectRequest));

        foreach (var exchange in rig.Exchanges)
        {
            Assert.True(exchange.Reply < exchange.Request, $"{exchange.Reply} >= {exchange.Request}");
        }

        var garbage = new byte[96];
        garbage[0] = SecureRecord.Init;
        garbage[1] = SecureRecord.Version;
        Assert.Equal(SecureInboundKind.Dropped, rig.ServerReceive(garbage).Kind);
    }

    [Fact]
    public void NoDiffieHellmanHappensBeforeAValidCookie()
    {
        var rig = new Rig();
        var before = X25519.OperationCount;
        var init = rig.ClientInit();
        var afterClient = X25519.OperationCount;
        var result = rig.ServerReceive(init);
        Assert.Equal(SecureInboundKind.Reply, result.Kind);
        Assert.Equal(SecureRecord.Retry, rig.ServerOutput(result)[0]);
        Assert.Equal(afterClient, X25519.OperationCount);
        Assert.True(afterClient >= before);
        Assert.Equal(0, rig.Server.SessionCount);

        var forged = (byte[])init.Clone();
        forged[7] = 0xAA;
        forged[6] = 16;
        var forgedResult = rig.ServerReceive(forged.Concat(new byte[16]).ToArray());
        Assert.Equal(SecureInboundKind.Reply, forgedResult.Kind);
        Assert.Equal(SecureRecord.Retry, rig.ServerOutput(forgedResult)[0]);
        Assert.Equal(afterClient, X25519.OperationCount);
        Assert.Equal(2, rig.ServerCounters.CookiesSent);
    }

    [Fact]
    public void CookieFromAnotherEndpointOrAfterExpiryIsRejected()
    {
        var rig = new Rig(server => server.CookieLifetimeMs = 1000);
        var init = rig.ClientInit();
        var retry = rig.ServerReceive(init);
        rig.ClientReceive(rig.ServerOutput(retry), out var secondInit);

        var other = SecureEndpoint.From(IPAddress.Parse("203.0.113.8"), 40000);
        Assert.Equal(SecureRecord.Retry, rig.ServerOutput(rig.ServerReceive(secondInit, other))[0]);

        rig.Now += 2500;
        var expired = rig.ServerReceive(secondInit);
        Assert.Equal(SecureRecord.Retry, rig.ServerOutput(expired)[0]);

        rig.Now = 1000;
        var rig2 = new Rig(server => server.CookieLifetimeMs = 1000);
        var first = rig2.ClientInit();
        rig2.ClientReceive(rig2.ServerOutput(rig2.ServerReceive(first)), out var cookied);
        rig2.Now += 1200;
        Assert.Equal(SecureRecord.Response, rig2.ServerOutput(rig2.ServerReceive(cookied))[0]);
    }

    [Fact]
    public void HandshakeWorksWithoutCookieWhenNotRequired()
    {
        var rig = new Rig(server => server.RequireCookie = false);
        Assert.True(rig.Handshake(ConnectRequest));
        Assert.Single(rig.Exchanges);
    }

    [Fact]
    public void RepeatedInitWithTheSameEphemeralGetsTheCachedResponseWithoutNewDiffieHellman()
    {
        var rig = new Rig(server => server.RequireCookie = false);
        var init = rig.ClientInit();
        var first = rig.ServerReceive(init);
        var firstBytes = rig.ServerOutput(first);
        var operations = X25519.OperationCount;
        var second = rig.ServerReceive(init);
        Assert.Equal(firstBytes, rig.ServerOutput(second));
        Assert.Equal(operations, X25519.OperationCount);
    }

    [Fact]
    public void WrongServerKeyTimesOutWithoutAnyReply()
    {
        var rig = new Rig(client: client => client.ServerPublicKeys = new List<byte[]> { X25519.GetPublicKey(X25519.GeneratePrivateKey()) });
        Assert.False(rig.Handshake(ConnectRequest));
        Assert.Empty(rig.Exchanges);
        Assert.True(rig.ServerCounters.HandshakesFailed >= 1);
        Assert.False(rig.Client.CheckTimeout());
        rig.Now += 6000;
        Assert.True(rig.Client.CheckTimeout());
        Assert.Equal(SecureClientState.TimedOut, rig.Client.State);
    }

    [Fact]
    public void WrongPreSharedKeyIsRejected()
    {
        var rig = new Rig(
            server => server.PreSharedKey = Enumerable.Repeat((byte)1, 32).ToArray(),
            client => client.PreSharedKey = Enumerable.Repeat((byte)2, 32).ToArray());
        Assert.False(rig.Handshake(ConnectRequest));
        Assert.True(rig.ServerCounters.HandshakesFailed >= 1);
    }

    [Fact]
    public void MissingPreSharedKeyOnEitherSideIsRejected()
    {
        var onlyServer = new Rig(server => server.PreSharedKey = Enumerable.Repeat((byte)1, 32).ToArray());
        Assert.False(onlyServer.Handshake(ConnectRequest));
        var onlyClient = new Rig(client: client => client.PreSharedKey = Enumerable.Repeat((byte)1, 32).ToArray());
        Assert.False(onlyClient.Handshake(ConnectRequest));
    }

    [Fact]
    public void MatchingPreSharedKeyWorks()
    {
        var psk = Enumerable.Repeat((byte)9, 32).ToArray();
        var rig = new Rig(server => server.PreSharedKey = psk, client => client.PreSharedKey = psk);
        Assert.True(rig.Handshake(ConnectRequest));
        Assert.Equal(ConnectRequest, rig.ServerPlain(rig.FirstData));
    }

    [Fact]
    public void ClientCanPinSeveralKeysAndServerRotationKeepsOldClientsWorking()
    {
        var oldKey = X25519.GeneratePrivateKey();
        var newKey = X25519.GeneratePrivateKey();
        var serverKeys = new List<byte[]> { newKey, oldKey };
        var withOld = new Rig(server => server.PrivateKeys = serverKeys, client => client.ServerPublicKeys = new List<byte[]> { X25519.GetPublicKey(oldKey) }, newKey);
        Assert.True(withOld.Handshake(ConnectRequest));

        var withBoth = new Rig(
            server => server.PrivateKeys = new List<byte[]> { newKey },
            client => client.ServerPublicKeys = new List<byte[]> { X25519.GetPublicKey(oldKey), X25519.GetPublicKey(newKey) },
            newKey);
        Assert.True(withBoth.Handshake(ConnectRequest));
        Assert.Equal(ConnectRequest, withBoth.ServerPlain(withBoth.FirstData));

        var retired = new Rig(
            server => server.PrivateKeys = new List<byte[]> { newKey },
            client => client.ServerPublicKeys = new List<byte[]> { X25519.GetPublicKey(oldKey) },
            newKey);
        Assert.False(retired.Handshake(ConnectRequest));
    }

    [Fact]
    public void BogusResponseDoesNotBreakTheHandshake()
    {
        var rig = new Rig(server => server.RequireCookie = false);
        rig.Client.Stash(ConnectRequest, 0, ConnectRequest.Length);
        var init = rig.ClientInit();
        var bogus = new byte[SecureRecord.ResponseSize];
        bogus[0] = SecureRecord.Response;
        new Random(1).NextBytes(bogus.AsSpan(1));
        rig.ClientReceive(bogus, out _);
        Assert.Equal(SecureClientState.Handshaking, rig.Client.State);
        Assert.Equal(1, rig.ClientCounters.HandshakesFailed);

        var result = rig.ServerReceive(init);
        rig.ClientReceive(rig.ServerOutput(result), out var data);
        Assert.Equal(SecureClientState.Established, rig.Client.State);
        Assert.Equal(ConnectRequest, rig.ServerPlain(data));
    }

    [Fact]
    public void ReplayedDataRecordIsDroppedAndCounted()
    {
        var rig = new Rig();
        Assert.True(rig.Handshake(ConnectRequest));
        Assert.Equal(ConnectRequest, rig.ServerPlain(rig.FirstData));
        Assert.Empty(rig.ServerPlain(rig.FirstData));
        Assert.Equal(1, rig.ServerCounters.Replays);
    }

    [Fact]
    public void EveryModifiedByteOfTheFirstDataRecordIsDroppedAndActivationIsNotTriggered()
    {
        var rig = new Rig();
        Assert.True(rig.Handshake(ConnectRequest));

        for (var i = 0; i < rig.FirstData.Length; i++)
        {
            var copy = (byte[])rig.FirstData.Clone();
            copy[i] ^= 0x04;
            Assert.Empty(rig.ServerPlain(copy));
        }

        Assert.False(rig.Server.HasActiveSession(rig.Endpoint));
        Assert.Equal(ConnectRequest, rig.ServerPlain(rig.FirstData));
    }

    [Fact]
    public void RunningSessionIsNotReplacedWhileItIsBusy()
    {
        var rig = new Rig(server => server.ReplaceIdleMs = 3000);
        Assert.True(rig.Handshake(ConnectRequest));
        rig.ServerPlain(rig.FirstData);
        var evicted = new List<object>();
        rig.Server.PeerEvicted += evicted.Add;
        rig.Server.Bind(rig.Endpoint, "victim");

        var attacker = new Rig(serverKey: rig.ServerKey);
        attacker.Client.Stash(ConnectRequest, 0, ConnectRequest.Length);
        rig.Now += 500;
        var first = rig.ServerReceive(attacker.ClientInit());
        Assert.Equal(SecureInboundKind.Reply, first.Kind);
        attacker.ClientReceive(rig.ServerOutput(first), out var secondInit);
        rig.Now += 500;
        var second = rig.ServerReceive(secondInit);
        Assert.Equal(SecureInboundKind.Dropped, second.Kind);
        Assert.Empty(evicted);
        Assert.True(rig.Server.HasActiveSession(rig.Endpoint));
    }

    [Fact]
    public void IdleSessionIsReplacedAndTheBoundPeerIsEvictedFirst()
    {
        var rig = new Rig(server => server.ReplaceIdleMs = 3000);
        Assert.True(rig.Handshake(ConnectRequest));
        rig.ServerPlain(rig.FirstData);
        var oldSession = rig.Server.Bind(rig.Endpoint, "victim");
        Assert.NotNull(oldSession);
        var evicted = new List<object>();
        var activeAtEviction = new List<SecureSession>();
        rig.Server.PeerEvicted += peer =>
        {
            evicted.Add(peer);
            activeAtEviction.Add(oldSession);
        };

        var newcomer = new Rig(serverKey: rig.ServerKey);
        newcomer.Client.Stash(ConnectRequest, 0, ConnectRequest.Length);
        rig.Now += 4000;
        var init = rig.ServerReceive(newcomer.ClientInit());
        newcomer.ClientReceive(rig.ServerOutput(init), out var cookied);
        var response = rig.ServerReceive(cookied);
        Assert.Equal(SecureInboundKind.Reply, response.Kind);
        newcomer.ClientReceive(rig.ServerOutput(response), out var data);

        Assert.Equal(ConnectRequest, rig.ServerPlain(data));
        Assert.Equal(new object[] { "victim" }, evicted);
        Assert.True(oldSession!.IsClosed);
        var current = rig.Server.Bind(rig.Endpoint, "attacker");
        Assert.NotNull(current);
        Assert.NotSame(oldSession, current);
        Assert.Equal("attacker", current!.Tag);
    }

    [Fact]
    public void NewSessionNeverInheritsABoundPeer()
    {
        var rig = new Rig(server => server.ReplaceIdleMs = 100);
        Assert.True(rig.Handshake(ConnectRequest));
        rig.ServerPlain(rig.FirstData);
        rig.Server.Bind(rig.Endpoint, "first");
        rig.Now += 500;
        var second = new Rig(serverKey: rig.ServerKey);
        second.Client.Stash(ConnectRequest, 0, ConnectRequest.Length);
        var step1 = rig.ServerReceive(second.ClientInit());
        second.ClientReceive(rig.ServerOutput(step1), out var cookied);
        var step2 = rig.ServerReceive(cookied);
        second.ClientReceive(rig.ServerOutput(step2), out var data);
        rig.ServerPlain(data);
        Assert.NotNull(rig.Server.Bind(rig.Endpoint, "second"));
    }

    [Fact]
    public void PlaintextIsDroppedInRequiredModeAndAllowedInOptionalUntilASessionExists()
    {
        var required = new Rig();
        Assert.Equal(SecureInboundKind.Dropped, required.ServerReceive(new byte[] { 5, 1, 2, 3 }).Kind);
        Assert.Equal(1, required.ServerCounters.PlaintextRejected);

        var optional = new Rig(server => server.AllowPlaintext = true);
        Assert.Equal(SecureInboundKind.Plain, optional.ServerReceive(new byte[] { 5, 1, 2, 3 }).Kind);
        Assert.True(optional.Handshake(ConnectRequest));
        optional.ServerPlain(optional.FirstData);
        Assert.Equal(SecureInboundKind.Dropped, optional.ServerReceive(new byte[] { 5, 1, 2, 3 }).Kind);
        var otherEndpoint = SecureEndpoint.From(IPAddress.Parse("198.51.100.1"), 1234);
        Assert.Equal(SecureInboundKind.Plain, optional.ServerReceive(new byte[] { 5, 1, 2, 3 }, otherEndpoint).Kind);
    }

    [Fact]
    public void ClientNeverAcceptsPlaintext()
    {
        var rig = new Rig();
        var result = rig.ClientReceive(new byte[] { 5, 1, 2, 3 }, out _);
        Assert.Equal(SecureClientInboundKind.Dropped, result.Kind);
        Assert.Equal(1, rig.ClientCounters.PlaintextRejected);
    }

    [Fact]
    public void OutboundWithoutSessionReportsNoSession()
    {
        var rig = new Rig();
        var buffer = new byte[100];
        Assert.Equal(-1, rig.Server.ProtectOutbound(rig.Endpoint, buffer, 0, 10));
        Assert.Equal(-1, rig.Client.Protect(buffer, 0, 10));
    }

    [Fact]
    public void GarbageFromUnknownEndpointsCreatesNoState()
    {
        var rig = new Rig();
        var random = new Random(7);

        for (var i = 0; i < 500; i++)
        {
            var packet = new byte[random.Next(1, 200)];
            random.NextBytes(packet);
            packet[0] = (byte)((packet[0] & 0xE0) | 0x1F);
            var endpoint = SecureEndpoint.From(IPAddress.Parse($"10.0.{i / 250}.{i % 250}"), 1000 + i);
            Assert.NotEqual(SecureInboundKind.Plain, rig.ServerReceive(packet, endpoint).Kind);
        }

        Assert.Equal(0, rig.Server.SessionCount);
    }

    [Fact]
    public void PerIpHandshakeLimitAndGlobalBudgetAreEnforced()
    {
        var limited = new Rig(server =>
        {
            server.RequireCookie = false;
            server.HandshakesPerIpPerSecond = 3;
        });
        var results = new List<SecureInboundKind>();

        for (var i = 0; i < 6; i++)
        {
            var client = new Rig(serverKey: limited.ServerKey);
            var endpoint = SecureEndpoint.From(IPAddress.Parse("203.0.113.7"), 40000 + i);
            results.Add(limited.ServerReceive(client.ClientInit(), endpoint).Kind);
        }

        Assert.Equal(3, results.Count(r => r == SecureInboundKind.Reply));
        Assert.Equal(3, limited.ServerCounters.HandshakesRateLimited);

        var budget = new Rig(server =>
        {
            server.RequireCookie = false;
            server.MaxHandshakesPerSecond = 2;
            server.HandshakesPerIpPerSecond = 0;
        });
        var accepted = 0;

        for (var i = 0; i < 5; i++)
        {
            var client = new Rig(serverKey: budget.ServerKey);
            var endpoint = SecureEndpoint.From(IPAddress.Parse($"203.0.113.{10 + i}"), 40000);

            if (budget.ServerReceive(client.ClientInit(), endpoint).Kind == SecureInboundKind.Reply)
            {
                accepted++;
            }
        }

        Assert.Equal(2, accepted);
        budget.Now += 1000;
        var late = new Rig(serverKey: budget.ServerKey);
        Assert.Equal(SecureInboundKind.Reply, budget.ServerReceive(late.ClientInit(), SecureEndpoint.From(IPAddress.Parse("203.0.113.99"), 1)).Kind);
    }

    [Fact]
    public void SweepRemovesStalePendingReleasedAndIdleSessions()
    {
        var rig = new Rig(server =>
        {
            server.RequireCookie = false;
            server.HandshakeTimeoutMs = 1000;
            server.SessionIdleTimeoutMs = 5000;
            server.ReleaseGraceMs = 500;
        });

        var pendingOnly = new Rig(serverKey: rig.ServerKey);
        rig.ServerReceive(pendingOnly.ClientInit(), SecureEndpoint.From(IPAddress.Parse("198.51.100.5"), 5));
        Assert.Equal(1, rig.Server.SessionCount);
        rig.Now += 1500;
        rig.Server.Sweep();
        Assert.Equal(0, rig.Server.SessionCount);

        Assert.True(rig.Handshake(ConnectRequest));
        rig.ServerPlain(rig.FirstData);
        var session = rig.Server.Bind(rig.Endpoint, "peer");
        rig.Server.Release(session!);
        rig.Now += 100;
        rig.Server.Sweep();
        Assert.True(rig.Server.HasActiveSession(rig.Endpoint));
        rig.Now += 600;
        rig.Server.Sweep();
        Assert.False(rig.Server.HasActiveSession(rig.Endpoint));

        var idle = new Rig(server =>
        {
            server.RequireCookie = false;
            server.SessionIdleTimeoutMs = 2000;
        });
        Assert.True(idle.Handshake(ConnectRequest));
        idle.ServerPlain(idle.FirstData);
        var evicted = new List<object>();
        idle.Server.PeerEvicted += evicted.Add;
        idle.Server.Bind(idle.Endpoint, "quiet");
        idle.Now += 2500;
        idle.Server.Sweep();
        Assert.Equal(new object[] { "quiet" }, evicted);
        Assert.Equal(0, idle.Server.SessionCount);
    }

    [Fact]
    public void ClientProtectsOnlyAfterTheHandshake()
    {
        var rig = new Rig();
        Assert.Equal(SecureClientState.Idle, rig.Client.State);
        rig.ClientInit();
        Assert.Equal(SecureClientState.Handshaking, rig.Client.State);
        Assert.Equal(-1, rig.Client.Protect(new byte[100], 0, 10));
        rig.Client.Reset();
        Assert.Equal(SecureClientState.Idle, rig.Client.State);
    }

    [Fact]
    public void ServerReportsPaddingInExtraPacketSize()
    {
        var plain = new Rig();
        Assert.Equal(26, plain.Server.ExtraPacketSize);
        var padded = new Rig(server => server.Session = new SecureSessionSettings { PaddingBlock = 64 });
        Assert.Equal(28 + 63, padded.Server.ExtraPacketSize);
    }
}
