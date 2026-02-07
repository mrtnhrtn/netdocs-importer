using System.Net;
using System.Text.Json;

namespace NetDocsImporter.NetDocs;

public sealed class NetDocumentsApiClient
{
    private readonly HttpClient _client;
    private readonly Func<NetDocumentsAuthContext> _authContextAccessor;
    private readonly Func<string> _apiBaseUrlAccessor;

    public NetDocumentsApiClient(
        INetDocumentsAuthService authService,
        Func<NetDocumentsAuthContext> authContextAccessor,
        Func<string> apiBaseUrlAccessor,
        HttpMessageHandler? innerHandler = null)
    {
        _authContextAccessor = authContextAccessor ?? throw new ArgumentNullException(nameof(authContextAccessor));
        _apiBaseUrlAccessor = apiBaseUrlAccessor ?? throw new ArgumentNullException(nameof(apiBaseUrlAccessor));

        var handler = new NetDocumentsAuthDelegatingHandler(
            authService,
            authContextAccessor,
            innerHandler ?? new HttpClientHandler());

        _client = new HttpClient(handler, disposeHandler: true);
    }

    public async Task<JsonDocument> GetJsonAsync(string relativeOrAbsolutePath, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(relativeOrAbsolutePath));
            return request;
        }, cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"NetDocuments API request failed ({(int)response.StatusCode}).");
        }

        return JsonDocument.Parse(content);
    }

    public async Task<T?> GetJsonAsync<T>(string relativeOrAbsolutePath, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(relativeOrAbsolutePath, cancellationToken);
        return document.RootElement.Deserialize<T>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        var delay = TimeSpan.FromMilliseconds(500);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var request = requestFactory();
            var response = await _client.SendAsync(request, cancellationToken);
            if (response.StatusCode != (HttpStatusCode)429 || attempt == maxAttempts)
            {
                return response;
            }

            var retryAfter = response.Headers.RetryAfter?.Delta ?? delay;
            response.Dispose();
            await Task.Delay(retryAfter, cancellationToken);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
        }

        throw new InvalidOperationException("NetDocuments API retry loop ended unexpectedly.");
    }

    private Uri BuildUri(string relativeOrAbsolutePath)
    {
        if (Uri.TryCreate(relativeOrAbsolutePath, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        var baseUrl = _apiBaseUrlAccessor().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("NetDocuments API base URL is not configured.");
        }

        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            baseUrl += "/";
        }

        var path = relativeOrAbsolutePath.TrimStart('/');
        return new Uri(new Uri(baseUrl), path);
    }
}
