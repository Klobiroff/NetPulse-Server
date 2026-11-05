using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetPulse.Common;
using NetPulse.Plugins;
using NetPulse.Server.Configuration;
using NetPulse.Server.Core;
using NetPulse.Server.Plugins;

namespace NetPulse.Server.Networking;

public sealed class Authenticator
{
    private readonly NetPulseOptions _options;
    private readonly PluginManager _plugins;
    private readonly ServerClock _clock;
    private readonly ILogger<Authenticator> _logger;

    public Authenticator(IOptions<NetPulseOptions> options, PluginManager plugins, ServerClock clock, ILogger<Authenticator> logger)
    {
        _options = options.Value;
        _plugins = plugins;
        _clock = clock;
        _logger = logger;
    }

    public void Handle(Peer peer, OperationRequest request) => _ = HandleAsync(peer, request);

    private async Task HandleAsync(Peer peer, OperationRequest request)
    {
        try
        {
            var data = AuthenticateRequest.FromParameters(request.Parameters);

            if (peer.Authenticated)
            {
                Respond(peer, request, ReturnCode.Ok, null);
                return;
            }

            if (!string.IsNullOrEmpty(_options.AppId) && !string.Equals(_options.AppId, data.AppId, StringComparison.Ordinal))
            {
                Respond(peer, request, ReturnCode.InvalidAppId, "Invalid AppId");
                return;
            }

            if (_options.AppVersionCheck && data.ProtocolVersion != Protocol.ProtocolVersion)
            {
                Respond(peer, request, ReturnCode.VersionMismatch, $"Server protocol is {Protocol.ProtocolVersion}, client is {data.ProtocolVersion}");
                return;
            }

            var userId = data.UserId;
            var nickName = data.NickName;

            if (_plugins.AuthPlugin is { } auth)
            {
                var timeout = TimeSpan.FromMilliseconds(_plugins.AuthTimeoutMs);
                using var cts = new CancellationTokenSource(timeout);
                AuthResult result;
                try
                {
                    result = await auth.AuthenticateAsync(
                        new AuthRequest
                        {
                            AppId = data.AppId,
                            UserId = data.UserId,
                            NickName = data.NickName,
                            AuthToken = data.AuthToken,
                            AuthData = data.AuthData,
                            RemoteEndpoint = peer.Endpoint
                        },
                        cts.Token).WaitAsync(timeout).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    _logger.LogWarning("Authentication plugin timed out for peer {Peer}", peer.Id);
                    Respond(peer, request, ReturnCode.AuthenticationFailed, "Authentication timed out");
                    await DisconnectAsync(peer);
                    return;
                }

                if (!result.Success)
                {
                    Respond(peer, request, ReturnCode.AuthenticationFailed, result.Error ?? "Authentication failed");
                    await DisconnectAsync(peer);
                    return;
                }

                userId = result.UserId ?? userId;
                nickName = result.NickName ?? nickName;
            }

            peer.UserId = string.IsNullOrWhiteSpace(userId) ? Guid.NewGuid().ToString("N") : userId;
            peer.NickName = string.IsNullOrWhiteSpace(nickName) ? $"Player{peer.Id}" : nickName;
            peer.Authenticated = true;

            var response = new AuthenticateResponse
            {
                UserId = peer.UserId,
                NickName = peer.NickName,
                ServerTimeTicks = _clock.NowMs
            };

            peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.Ok, null, response.ToParameters());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authentication failed for peer {Peer}", peer.Id);
            Respond(peer, request, ReturnCode.AuthenticationFailed, "Authentication error");
        }
    }

    private static async Task DisconnectAsync(Peer peer)
    {
        await Task.Delay(250).ConfigureAwait(false);
        peer.Disconnect("AuthenticationFailed");
    }

    private static void Respond(Peer peer, OperationRequest request, short code, string? message) =>
        peer.SendResponse(request.OpCode, request.RequestId, code, message);
}
