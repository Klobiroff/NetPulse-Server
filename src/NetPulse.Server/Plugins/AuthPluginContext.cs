using Microsoft.Extensions.Logging;
using NetPulse.Plugins;

namespace NetPulse.Server.Plugins;

public sealed class AuthPluginContext : IAuthPluginContext
{
    private readonly IHttpClientFactory _httpFactory;

    public AuthPluginContext(ILogger logger, IHttpClientFactory httpFactory)
    {
        Logger = logger;
        _httpFactory = httpFactory;
    }

    public ILogger Logger { get; }

    public Task<HttpResponseMessage> HttpAsync(HttpRequestMessage request, CancellationToken ct) =>
        _httpFactory.CreateClient(PluginHost.HttpClientName).SendAsync(request, ct);
}
