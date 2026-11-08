using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetPulse.Common;
using NetPulse.Common.Security;
using NetPulse.Server.Core;
using NetPulse.Server.Networking;
using NetPulse.TestClient;

namespace NetPulse.Server.Tests;

public abstract class HardeningTestBase : ServerTestBase
{
    protected NetPulseServer Server => Services.GetRequiredService<NetPulseServer>();

    protected ServerMetrics Metrics => Services.GetRequiredService<ServerMetrics>();

    protected static async Task<string> DisconnectReasonAsync(BotClient bot)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (bot.DisconnectReason is { } reason)
            {
                return reason;
            }

            await Task.Delay(20);
        }

        return string.Empty;
    }
}

public sealed class ConnectionLimitTests : HardeningTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:Security:MaxConnectionsPerIp"] = "2",
        ["NetPulse:Security:ConnectionAttemptsPerIpPerSecond"] = "0"
    };

    [Fact]
    public async Task ThirdConnectionFromTheSameAddressIsRejected()
    {
        await ConnectAsync("one");
        await ConnectAsync("two");
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync("three"));
        Assert.True(Metrics.ConnectionsRejected >= 1);
        Assert.Equal(2, Server.PeerCount);
    }

    [Fact]
    public async Task SlotIsFreedWhenAPeerDisconnects()
    {
        var first = await ConnectAsync("one");
        await ConnectAsync("two");
        first.Dispose();
        await Eventually(() => Server.PeerCount == 1);
        await ConnectAsync("three");
        Assert.Equal(2, Server.PeerCount);
    }
}

public sealed class ConnectionAttemptRateTests : HardeningTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:Security:ConnectionAttemptsPerIpPerSecond"] = "1"
    };

    [Fact]
    public async Task BurstOfConnectionAttemptsIsThrottled()
    {
        var results = new List<bool>();

        for (var i = 0; i < 6; i++)
        {
            try
            {
                var bot = new BotClient($"burst{i}");
                await bot.ConnectAsync("127.0.0.1", Port, timeout: TimeSpan.FromMilliseconds(1500));
                results.Add(true);
                bot.Dispose();
            }
            catch (Exception)
            {
                results.Add(false);
            }
        }

        Assert.Contains(true, results);
        Assert.Contains(false, results);
        Assert.True(Metrics.ConnectionsRejected >= 1);
    }
}

public sealed class AuthenticationLimitTests : HardeningTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:Security:AuthDeadlineMs"] = "1500",
        ["NetPulse:Security:MaxAuthAttempts"] = "2",
        ["NetPulse:AppId"] = "right"
    };

    [Fact]
    public async Task UnauthenticatedPeerIsDisconnectedAfterTheDeadline()
    {
        var bot = await ConnectAsync("lazy", authenticate: false);
        var reason = await DisconnectReasonAsync(bot);
        Assert.Contains("AuthTimeout", reason);
        Assert.True(Metrics.AuthTimeouts >= 1);
    }

    [Fact]
    public async Task AuthenticatedPeerIsNotTouchedByTheDeadline()
    {
        var bot = await ConnectAsync("busy", appId: "right");
        await Task.Delay(2500);
        Assert.True(bot.IsConnected);
        Assert.Null(bot.DisconnectReason);
    }

    [Fact]
    public async Task TooManyFailedAttemptsDisconnect()
    {
        var bot = await ConnectAsync("guesser", authenticate: false);
        var first = await bot.AuthenticateAsync(appId: "wrong1");
        var second = await bot.AuthenticateAsync(appId: "wrong2");
        Assert.Equal(ReturnCode.InvalidAppId, first.ReturnCode);
        Assert.Equal(ReturnCode.InvalidAppId, second.ReturnCode);
        var third = await bot.AuthenticateAsync(appId: "right");
        Assert.Equal(ReturnCode.AuthenticationFailed, third.ReturnCode);
        Assert.Contains("TooManyAuthAttempts", await DisconnectReasonAsync(bot));
    }
}

public sealed class RateLimitTests : HardeningTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:Security:MaxMessagesPerSecond"] = "20",
        ["NetPulse:Security:MessageBurst"] = "20",
        ["NetPulse:Security:MaxBytesPerSecond"] = "20000",
        ["NetPulse:Security:ByteBurst"] = "20000",
        ["NetPulse:Security:MaxMessageBytes"] = "8000"
    };

    [Fact]
    public async Task MessageFloodIsDisconnectedWithRateLimitedAndOthersAreUnaffected()
    {
        var flooder = await ConnectAsync("flooder");
        var normal = await ConnectAsync("normal");

        for (var i = 0; i < 300; i++)
        {
            flooder.SendPing();
        }

        Assert.Contains("RateLimited", await DisconnectReasonAsync(flooder));
        Assert.True(Metrics.PeersRateLimited >= 1);
        var response = await normal.JoinLobbyAsync();
        Assert.Equal(ReturnCode.Ok, response.ReturnCode);
    }

    [Fact]
    public async Task OversizedMessageIsRejectedWithRateLimited()
    {
        var bot = await ConnectAsync("big");
        await JoinAsync(bot, "room");
        bot.Send(new OperationRequest
        {
            OpCode = OpCode.RaiseEvent,
            RequestId = 0,
            Parameters = new RaiseEventRequest { EventCode = 20, Data = new byte[9000], Receivers = ReceiverGroup.Others }.ToParameters()
        });

        Assert.Contains("RateLimited", await DisconnectReasonAsync(bot));
    }

    [Fact]
    public async Task ByteBudgetIsEnforced()
    {
        var bot = await ConnectAsync("heavy");
        await JoinAsync(bot, "room");

        for (var i = 0; i < 6; i++)
        {
            bot.Send(new OperationRequest
            {
                OpCode = OpCode.RaiseEvent,
                RequestId = 0,
                Parameters = new RaiseEventRequest { EventCode = 20, Data = new byte[7000], Receivers = ReceiverGroup.Others }.ToParameters()
            });
        }

        Assert.Contains("RateLimited", await DisconnectReasonAsync(bot));
    }

    [Fact]
    public async Task NormalTrafficPassesTheLimiter()
    {
        var bot = await ConnectAsync("calm");
        await JoinAsync(bot, "room");

        for (var i = 0; i < 10; i++)
        {
            var response = await bot.RaiseEventAsync(20, "ok", ReceiverGroup.Others);
            Assert.Equal(ReturnCode.Ok, response.ReturnCode);
            await Task.Delay(80);
        }

        Assert.True(bot.IsConnected);
    }
}

public sealed class SizeLimitTests : HardeningTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:Security:MaxPropertiesPerRoom"] = "10",
        ["NetPulse:Security:MaxPropertyBytes"] = "200",
        ["NetPulse:Security:MaxTargetActors"] = "3",
        ["NetPulse:Security:MaxCachedEventsPerRoom"] = "3",
        ["NetPulse:Security:MaxRoomNameLength"] = "16",
        ["NetPulse:Security:MaxUserIdLength"] = "20",
        ["NetPulse:Security:MaxNickNameLength"] = "10",
        ["NetPulse:Security:MaxAuthTokenLength"] = "30"
    };

    [Fact]
    public async Task AuthenticationFieldsAreRefusedNotTruncated()
    {
        var bot = await ConnectAsync("limits", authenticate: false);
        Assert.Equal(ReturnCode.InvalidRequest, (await bot.AuthenticateAsync(userId: new string('u', 21))).ReturnCode);
        Assert.Equal(ReturnCode.InvalidRequest, (await bot.AuthenticateAsync(userId: "ok", authToken: new string('t', 31))).ReturnCode);
        var nick = await bot.RequestAsync(OpCode.Authenticate, new AuthenticateRequest { UserId = "ok", NickName = new string('n', 11) }.ToParameters());
        Assert.Equal(ReturnCode.InvalidRequest, nick.ReturnCode);
        var control = await bot.RequestAsync(OpCode.Authenticate, new AuthenticateRequest { UserId = "bad\nuser", NickName = "n" }.ToParameters());
        Assert.Equal(ReturnCode.InvalidRequest, control.ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await bot.AuthenticateAsync(userId: "fine", authToken: new string('t', 30))).ReturnCode);
        Assert.Equal("fine", bot.UserId);
    }

    [Fact]
    public async Task RoomNamesAreValidated()
    {
        var bot = await ConnectAsync("rooms");
        Assert.Equal(ReturnCode.InvalidRequest, (await bot.CreateRoomAsync(new string('r', 17))).ReturnCode);
        Assert.Equal(ReturnCode.InvalidRequest, (await bot.JoinRoomAsync(new string('r', 17))).ReturnCode);
        Assert.Equal(ReturnCode.InvalidRequest, (await bot.JoinOrCreateRoomAsync("bad\nname")).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await bot.JoinOrCreateRoomAsync(new string('r', 16))).ReturnCode);
    }

    [Fact]
    public async Task RoomAndPropertyLimitsReturnInvalidRequest()
    {
        var bot = await ConnectAsync("props");
        var tooMany = new PropertyTable();

        for (var i = 0; i < 12; i++)
        {
            tooMany[$"k{i}"] = i;
        }

        var create = await bot.CreateRoomAsync("room", new RoomOptions { CustomProperties = tooMany });
        Assert.Equal(ReturnCode.InvalidRequest, create.ReturnCode);

        await JoinAsync(bot, "room2");
        var big = new PropertyTable { ["blob"] = new string('x', 500) };
        Assert.Equal(ReturnCode.InvalidRequest, (await bot.SetRoomPropertiesAsync(big)).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await bot.SetRoomPropertiesAsync(new PropertyTable { ["small"] = "x" })).ReturnCode);

        var many = new PropertyTable();

        for (var i = 0; i < 12; i++)
        {
            many[$"p{i}"] = i;
        }

        Assert.Equal(ReturnCode.InvalidRequest, (await bot.SetRoomPropertiesAsync(many)).ReturnCode);
        Assert.False(bot.RoomProperties.ContainsKey("p0"));
    }

    [Fact]
    public async Task EventTargetsAndCacheAreBounded()
    {
        var bot = await ConnectAsync("events");
        await JoinAsync(bot, "room");

        var targets = await bot.RaiseEventAsync(20, "x", ReceiverGroup.Targets, targets: new[] { 1, 2, 3, 4 });
        Assert.Equal(ReturnCode.InvalidRequest, targets.ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await bot.RaiseEventAsync(20, "x", ReceiverGroup.Targets, targets: new[] { 1, 2, 3 })).ReturnCode);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(ReturnCode.Ok, (await bot.RaiseEventAsync(21, i, ReceiverGroup.Others, CachingOption.AddToRoomCache)).ReturnCode);
        }

        var overflow = await bot.RaiseEventAsync(21, 99, ReceiverGroup.Others, CachingOption.AddToRoomCache);
        Assert.Equal(ReturnCode.InvalidRequest, overflow.ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await bot.RaiseEventAsync(21, 100, ReceiverGroup.Others)).ReturnCode);
        Assert.True(Metrics.LimitViolations >= 2);
    }

    [Fact]
    public void DeeplyNestedContainerBombIsRefused()
    {
        var writer = new NetWriter(4096);
        writer.WriteByte(NetTypeCode.ObjectArray);
        writer.WriteVarUInt(5000);

        for (var i = 0; i < 5000; i++)
        {
            writer.WriteByte(NetTypeCode.ObjectArray);
            writer.WriteVarUInt(0);
        }

        var reader = new NetReader(writer.ToArray());
        Assert.Throws<NetSerializationException>(() => reader.ReadObject());
    }

    [Fact]
    public void DeclaredLengthsLargerThanThePayloadAreRefusedBeforeAllocation()
    {
        var writer = new NetWriter(64);
        writer.WriteByte(NetTypeCode.IntArray);
        writer.WriteVarUInt(uint.MaxValue / 8);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<NetSerializationException>(() => new NetReader(writer.ToArray()).ReadObject());
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024);

        var text = new NetWriter(64);
        text.WriteByte(NetTypeCode.String);
        text.WriteVarUInt(int.MaxValue);
        Assert.Throws<NetSerializationException>(() => new NetReader(text.ToArray()).ReadObject());
    }
}

public sealed class RejoinTokenTests : HardeningTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:Security:RequireRejoinToken"] = "true"
    };

    [Fact]
    public async Task StrangerWithTheSameUserIdCannotTakeAnInactiveSeat()
    {
        var options = new RoomOptions { PlayerTtlMs = 30000 };
        var owner = await ConnectAsync("owner");
        var join = await owner.JoinOrCreateRoomAsync("room", options);
        Assert.Equal(ReturnCode.Ok, join.ReturnCode);
        Assert.False(string.IsNullOrEmpty(owner.RejoinToken));
        var token = owner.RejoinToken;

        await owner.LeaveRoomAsync(becomeInactive: true);

        var thief = await ConnectAsync("thief", authenticate: false);
        await thief.AuthenticateAsync(userId: "owner");
        var stolen = await thief.JoinRoomAsync("room");
        Assert.Equal(ReturnCode.Forbidden, stolen.ReturnCode);

        var back = await owner.JoinRoomAsync("room");
        Assert.Equal(ReturnCode.Ok, back.ReturnCode);
        Assert.NotEqual(token, owner.RejoinToken);
    }

}

public sealed class RejoinTokenOffTests : HardeningTestBase
{
    [Fact]
    public async Task TokensAreNotIssuedAndRejoinByUserIdStillWorksWhenTheFeatureIsOff()
    {
        var options = new RoomOptions { PlayerTtlMs = 30000 };
        var owner = await ConnectAsync("plain");
        Assert.Equal(ReturnCode.Ok, (await owner.JoinOrCreateRoomAsync("room2", options)).ReturnCode);
        Assert.True(string.IsNullOrEmpty(owner.RejoinToken));
        await owner.LeaveRoomAsync(becomeInactive: true);
        var again = await owner.JoinRoomAsync("room2");
        Assert.Equal(ReturnCode.Ok, again.ReturnCode);
    }
}

public sealed class LogHygieneTests : SecureServerTestBase
{
    private readonly byte[] _psk = Enumerable.Range(1, 32).Select(i => (byte)(i * 3)).ToArray();

    protected override string Mode => "Required";

    protected override Dictionary<string, string?> Extra => new()
    {
        ["Logging:LogLevel:Default"] = "Trace",
        ["NetPulse:Encryption:PreSharedKey"] = SecurityKeyText.ToText(_psk)
    };

    [Fact]
    public async Task SecretsNeverReachTheLogs()
    {
        var bot = await ConnectAsync("logger", security: ClientSettings(s => s.PreSharedKey = _psk), authToken: "tok-NEVER-LOG-THIS");
        await JoinAsync(bot, "room");
        bot.Dispose();
        await Eventually(() => Server.PeerCount == 0);

        var text = string.Join('\n', Logs.Entries.Select(e => e.Message));
        Assert.NotEmpty(Logs.Entries);
        Assert.DoesNotContain("tok-NEVER-LOG-THIS", text);
        Assert.DoesNotContain(SecurityKeyText.ToText(PrivateKey), text);
        Assert.DoesNotContain(SecurityKeyText.ToText(_psk), text);
        Assert.Contains(SecurityKeyText.ToText(PublicKey), text);
    }

    [Fact]
    public void ControlCharactersAreNormalizedForLogs()
    {
        Assert.Equal("a?b?c", LogText.Clean("a\nb\rc"));
        Assert.Equal(new string('x', 128) + "~", LogText.Clean(new string('x', 200)));
        Assert.True(LogText.ContainsControl("bad\u001b[0m"));
        Assert.False(LogText.ContainsControl("fine text"));
    }
}
