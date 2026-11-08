using Microsoft.Extensions.Logging;
using NetPulse.Common;

namespace NetPulse.Plugins;

public sealed class AuthRequest
{
    public string? AppId { get; init; }

    public string? UserId { get; init; }

    public string? NickName { get; init; }

    public string? AuthToken { get; init; }

    public PropertyTable? AuthData { get; init; }

    public string? RemoteEndpoint { get; init; }

    public bool IsEncrypted { get; init; }

    public byte[]? ChannelBinding { get; init; }
}

public sealed class AuthResult
{
    public bool Success { get; init; }

    public string? UserId { get; init; }

    public string? NickName { get; init; }

    public string? Error { get; init; }

    public static AuthResult Ok(string? userId = null, string? nickName = null) => new()
    {
        Success = true,
        UserId = userId,
        NickName = nickName
    };

    public static AuthResult Rejected(string error) => new()
    {
        Success = false,
        Error = error
    };
}

public interface IAuthPlugin
{
    Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct);
}

public interface IAuthPluginContext
{
    ILogger Logger { get; }

    Task<HttpResponseMessage> HttpAsync(HttpRequestMessage request, CancellationToken ct);
}
