using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetPulse.Plugins.AuthSample;

public sealed class AuthSamplePlugin : IAuthPlugin
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IAuthPluginContext _context;
    private readonly bool _useHttp;
    private readonly string _secret = string.Empty;
    private readonly string _url = string.Empty;
    private readonly TimeProvider _time;

    public AuthSamplePlugin(IReadOnlyDictionary<string, string> config, IAuthPluginContext context)
        : this(config, context, TimeProvider.System)
    {
    }

    public AuthSamplePlugin(IReadOnlyDictionary<string, string> config, IAuthPluginContext context, TimeProvider time)
    {
        _context = context;
        _time = time;
        var mode = config.GetValueOrDefault("Mode", "Hmac");

        if (string.Equals(mode, "Http", StringComparison.OrdinalIgnoreCase))
        {
            _useHttp = true;
            _url = config.GetValueOrDefault("Url", string.Empty);

            if (!Uri.TryCreate(_url, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("AuthSample Mode=Http requires a valid absolute Url setting");
            }
        }
        else if (string.Equals(mode, "Hmac", StringComparison.OrdinalIgnoreCase))
        {
            _secret = config.GetValueOrDefault("Secret", string.Empty);

            if (_secret.Length == 0)
            {
                throw new InvalidOperationException("AuthSample Mode=Hmac requires a Secret setting");
            }
        }
        else
        {
            throw new InvalidOperationException($"AuthSample Mode must be Hmac or Http, got '{mode}'");
        }

        context.Logger.LogInformation("AuthSample enabled in {Mode} mode", _useHttp ? "Http" : "Hmac");
    }

    public Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct) =>
        _useHttp ? AuthenticateHttpAsync(request, ct) : Task.FromResult(AuthenticateHmac(request));

    private AuthResult AuthenticateHmac(AuthRequest request)
    {
        var error = HmacTokens.TryValidate(_secret, request.AuthToken, _time.GetUtcNow(), out var userId, out var nickName);

        return error switch
        {
            TokenError.None => AuthResult.Ok(userId, nickName),
            TokenError.Expired => AuthResult.Rejected("Token expired"),
            _ => AuthResult.Rejected("Invalid token")
        };
    }

    private async Task<AuthResult> AuthenticateHttpAsync(AuthRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = JsonContent.Create(request, options: Json)
        };

        try
        {
            using var response = await _context.HttpAsync(message, ct);

            if (!response.IsSuccessStatusCode)
            {
                _context.Logger.LogWarning("Auth service answered {Status}", (int)response.StatusCode);
                return AuthResult.Rejected("Authentication service error");
            }

            var body = await response.Content.ReadFromJsonAsync<HttpAuthResponse>(Json, ct);

            if (body is null)
            {
                return AuthResult.Rejected("Malformed authentication response");
            }

            return body.Ok ? AuthResult.Ok(body.UserId, body.NickName) : AuthResult.Rejected(body.Message ?? "Authentication rejected");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _context.Logger.LogWarning("Auth service returned a malformed response");
            return AuthResult.Rejected("Malformed authentication response");
        }
        catch (HttpRequestException ex)
        {
            _context.Logger.LogWarning(ex, "Auth service is unreachable");
            return AuthResult.Rejected("Authentication service unavailable");
        }
    }

    private sealed class HttpAuthResponse
    {
        public bool Ok { get; set; }

        public string? UserId { get; set; }

        public string? NickName { get; set; }

        public string? Message { get; set; }
    }
}
