using System.Net;
using System.Text.Json;
using System.Diagnostics;
using NetDocsImporter.Core;

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
        var requestUri = BuildUri(relativeOrAbsolutePath);
        var stopwatch = Stopwatch.StartNew();
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            return request;
        }, cancellationToken);
        stopwatch.Stop();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogHttpFailure("GET", relativeOrAbsolutePath, requestUri, response, content);
            throw new InvalidOperationException(
                $"NetDocuments API request failed ({(int)response.StatusCode} {response.ReasonPhrase}) for '{relativeOrAbsolutePath}'. Snippet: {BuildSnippet(content)}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var snippet = content.Length > 180 ? content[..180] : content;
            Trace.WriteLine($"ND-HTTP non-json method=GET path='{relativeOrAbsolutePath}' url='{requestUri}' status={(int)response.StatusCode} latencyMs={stopwatch.ElapsedMilliseconds} mediaType='{mediaType}' snippet='{SensitiveDataRedactor.RedactBearerTokens(snippet)}'");
            throw new InvalidOperationException(
                $"NetDocuments API returned non-JSON content ('{mediaType}') for '{relativeOrAbsolutePath}'. Snippet: {snippet}");
        }

        Trace.WriteLine(
            $"ND-HTTP success method=GET path='{relativeOrAbsolutePath}' url='{requestUri}' status={(int)response.StatusCode} latencyMs={stopwatch.ElapsedMilliseconds}");

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

    public async Task PostAsync(
        string relativeOrAbsolutePath,
        HttpContent content,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildUri(relativeOrAbsolutePath);
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await _client.SendAsync(request, cancellationToken);
        stopwatch.Stop();
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            LogHttpFailure("POST", relativeOrAbsolutePath, requestUri, response, responseContent);
            throw new InvalidOperationException(
                $"NetDocuments API request failed ({(int)response.StatusCode} {response.ReasonPhrase}) for '{relativeOrAbsolutePath}'. Snippet: {BuildSnippet(responseContent)}");
        }

        Trace.WriteLine(
            $"ND-HTTP success method=POST path='{relativeOrAbsolutePath}' url='{requestUri}' status={(int)response.StatusCode} latencyMs={stopwatch.ElapsedMilliseconds}");
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
            Trace.WriteLine($"ND-HTTP throttled status=429 attempt={attempt}/{maxAttempts} retryAfterMs={retryAfter.TotalMilliseconds:F0}");
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

    private static void LogHttpFailure(string method, string path, Uri requestUri, HttpResponseMessage response, string body)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var snippet = BuildSnippet(body);
        Trace.WriteLine(
            $"ND-HTTP error method={method} path='{path}' url='{requestUri}' status={(int)response.StatusCode} reason='{response.ReasonPhrase}' mediaType='{mediaType}' snippet='{snippet}'");
    }

    private static string BuildSnippet(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var snippet = text.Length > 240 ? text[..240] : text;
        return SensitiveDataRedactor.RedactBearerTokens(snippet);
    }
}
