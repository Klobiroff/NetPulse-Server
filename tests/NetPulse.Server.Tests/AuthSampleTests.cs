using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetPulse.Common;
using NetPulse.Plugins;
using NetPulse.Plugins.AuthSample;
using NetPulse.Server.Plugins;

namespace NetPulse.Server.Tests;

public sealed class FakeAuthContext : IAuthPluginContext
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public FakeAuthContext(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? handler = null) =>
        _handler = handler ?? ((_, _) => Task.FromException<HttpResponseMessage>(new InvalidOperationException("no HTTP expected")));

    public ILogger Logger { get; } = NullLogger.Instance;

    public Task<HttpResponseMessage> HttpAsync(HttpRequestMessage request, CancellationToken ct) => _handler(request, ct);
}

public sealed class FixedTime : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTime(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

public sealed class HmacAuthTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static AuthSamplePlugin Plugin(string secret = "s3cret") =>
        new(new Dictionary<string, string> { ["Mode"] = "Hmac", ["Secret"] = secret }, new FakeAuthContext(), new FixedTime(Now));

    [Fact]
    public async Task ValidTokenYieldsAuthoritativeIdentity()
    {
        var token = HmacTokens.Create("s3cret", "user-42", "Alice", Now.AddHours(1));

        var result = await Plugin().AuthenticateAsync(new AuthRequest { AuthToken = token, UserId = "spoofed" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("user-42", result.UserId);
        Assert.Equal("Alice", result.NickName);
    }

    [Fact]
    public async Task ExpiredTokenIsRejected()
    {
        var token = HmacTokens.Create("s3cret", "u", "n", Now.AddSeconds(-1));

        var result = await Plugin().AuthenticateAsync(new AuthRequest { AuthToken = token }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("expired", result.Error);
    }

    [Fact]
    public async Task TamperedPayloadIsRejected()
    {
        var token = HmacTokens.Create("s3cret", "u", "n", Now.AddHours(1));
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin|n|{Now.AddHours(1).ToUnixTimeSeconds()}")).TrimEnd('=') + "." + token.Split('.')[1];

        var result = await Plugin().AuthenticateAsync(new AuthRequest { AuthToken = forged }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task WrongSecretAndGarbageAreRejected()
    {
        var token = HmacTokens.Create("other-secret", "u", "n", Now.AddHours(1));
        var plugin = Plugin();

        Assert.False((await plugin.AuthenticateAsync(new AuthRequest { AuthToken = token }, CancellationToken.None)).Success);
        Assert.False((await plugin.AuthenticateAsync(new AuthRequest { AuthToken = "not-a-token" }, CancellationToken.None)).Success);
        Assert.False((await plugin.AuthenticateAsync(new AuthRequest { AuthToken = null }, CancellationToken.None)).Success);
    }

    [Fact]
    public void MissingSecretFailsFast()
    {
        var config = new Dictionary<string, string> { ["Mode"] = "Hmac" };

        Assert.Throws<InvalidOperationException>(() => new AuthSamplePlugin(config, new FakeAuthContext()));
    }
}

public sealed class HttpAuthTests
{
    private static AuthSamplePlugin Plugin(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new Dictionary<string, string> { ["Mode"] = "Http", ["Url"] = "https://auth.example/verify" }, new FakeAuthContext(handler));

    private static Task<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    [Fact]
    public async Task OkResponseProvidesIdentityAndPostsTheRequestAsJson()
    {
        HttpRequestMessage? seen = null;
        string? sentBody = null;

        var plugin = Plugin(async (request, ct) =>
        {
            seen = request;
            sentBody = await request.Content!.ReadAsStringAsync(ct);
            return await Json("""{ "ok": true, "userId": "u1", "nickName": "Bob", "message": "", "data": {} }""");
        });

        var result = await plugin.AuthenticateAsync(new AuthRequest { AuthToken = "tok", UserId = "x" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("u1", result.UserId);
        Assert.Equal("Bob", result.NickName);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("https://auth.example/verify", seen.RequestUri!.ToString());
        Assert.Equal("tok", JsonDocument.Parse(sentBody!).RootElement.GetProperty("authToken").GetString());
    }

    [Fact]
    public async Task RejectedResponseCarriesTheMessage()
    {
        var plugin = Plugin((_, _) => Json("""{ "ok": false, "message": "banned" }"""));

        var result = await plugin.AuthenticateAsync(new AuthRequest(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("banned", result.Error);
    }

    [Fact]
    public async Task MalformedAndErrorResponsesAreRejected()
    {
        Assert.False((await Plugin((_, _) => Json("not json")).AuthenticateAsync(new AuthRequest(), CancellationToken.None)).Success);
        Assert.False((await Plugin((_, _) => Json("null")).AuthenticateAsync(new AuthRequest(), CancellationToken.None)).Success);
        Assert.False((await Plugin((_, _) => Json("{}", HttpStatusCode.InternalServerError)).AuthenticateAsync(new AuthRequest(), CancellationToken.None)).Success);
    }

    [Fact]
    public async Task CancellationIsPropagatedWhenTheServiceHangs()
    {
        var plugin = Plugin(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.AuthenticateAsync(new AuthRequest(), cts.Token));
    }
}

public sealed class AuthIntegrationTests : ServerTestBase
{
    protected override void RegisterPlugins(PluginManager plugins) =>
        plugins.AuthPlugin = new AuthSamplePlugin(
            new Dictionary<string, string> { ["Mode"] = "Hmac", ["Secret"] = "integration-secret" },
            new FakeAuthContext());

    [Fact]
    public async Task ValidTokenSetsServerSideIdentity()
    {
        var client = await ConnectAsync("anything", authenticate: false);
        var token = HmacTokens.Create("integration-secret", "real-user", "RealNick", DateTimeOffset.UtcNow.AddMinutes(5));

        var response = await client.AuthenticateAsync(userId: "claimed-user", authToken: token);

        Assert.Equal(ReturnCode.Ok, response.ReturnCode);
        Assert.Equal("real-user", client.UserId);
        Assert.Equal("RealNick", client.NickName);
    }

    [Fact]
    public async Task BadTokenGetsAuthenticationFailedAndIsDisconnected()
    {
        var client = await ConnectAsync("bad", authenticate: false);

        var response = await client.AuthenticateAsync(authToken: "garbage");

        Assert.Equal(ReturnCode.AuthenticationFailed, response.ReturnCode);
        await Eventually(() => client.DisconnectReason is not null);
        Assert.Contains("AuthenticationFailed", client.DisconnectReason);
    }
}
