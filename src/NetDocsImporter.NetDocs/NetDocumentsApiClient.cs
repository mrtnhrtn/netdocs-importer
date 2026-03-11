using System.Net;
using System.Text.Json;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using NetDocsImporter.Core;

namespace NetDocsImporter.NetDocs;

/// <summary>
/// Wraps HTTP calls to NetDocuments APIs with authentication, retry-on-throttle, and trace logging.
/// </summary>
public sealed class NetDocumentsApiClient
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReferenceRegex = new(@"Reference\s*#\s*(?<value>[^\s<]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] ApiCostHeaderNames = ["X-ND-API-Cost", "X-API-Cost"];
    private static readonly string[] ApiRemainingHeaderNames = ["X-ND-API-Remaining", "X-RateLimit-Remaining"];
    private static readonly string[] ApiTotalAvailablePropertyNames = ["totalAvailable", "totalavailable", "total_available", "apiTotalAvailable"];
    private static readonly string[] ApiSpendSoFarPropertyNames = ["spendSoFar", "spendsofar", "spentSoFar", "spentsofar", "spend_so_far", "spent_so_far", "apiSpendSoFar"];

    private readonly HttpClient _client;
    private readonly Func<NetDocumentsAuthContext> _authContextAccessor;
    private readonly Func<string> _apiBaseUrlAccessor;
    private readonly AsyncLocal<Action<NdApiCallTrace>?> _callTraceObserver = new();

    /// <summary>
    /// Initializes a NetDocuments API client instance.
    /// </summary>
    /// <param name="authService">Authentication service used to attach bearer tokens.</param>
    /// <param name="authContextAccessor">Accessor for the active OAuth client context.</param>
    /// <param name="apiBaseUrlAccessor">Accessor for the active NetDocuments API base URL.</param>
    /// <param name="innerHandler">Optional inner HTTP handler, primarily for tests.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required accessor is null.</exception>
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
        // Use caller-provided cancellation/request timeouts instead of HttpClient's 100-second default.
        _client.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Executes a GET request and parses the response body as JSON.
    /// </summary>
    /// <param name="relativeOrAbsolutePath">Relative API path or absolute URL.</param>
    /// <param name="cancellationToken">Token used to cancel the HTTP call.</param>
    /// <returns>The parsed JSON document.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the response is non-success or not JSON.</exception>
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

        var content = await ReadContentAsStringAsync(response.Content, cancellationToken);
        var usage = ExtractApiUsage(response, content);
        if (!response.IsSuccessStatusCode)
        {
            EmitApiCallTrace(new NdApiCallTrace
            {
                Method = "GET",
                RelativePath = relativeOrAbsolutePath,
                Url = requestUri.ToString(),
                StatusCode = (int)response.StatusCode,
                Succeeded = false,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ResponseLength = content.Length,
                ResponsePreview = BuildResponsePreview(content),
                ErrorMessage = $"{(int)response.StatusCode} {response.ReasonPhrase}",
                ApiCost = usage.ApiCost,
                ApiRemaining = usage.ApiRemaining,
                ApiSpendSoFar = usage.ApiSpendSoFar,
                ApiTotalAvailable = usage.ApiTotalAvailable,
                ApiUsageSource = usage.Source
            });
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
            $"ND-HTTP success method=GET path='{relativeOrAbsolutePath}' url='{requestUri}' status={(int)response.StatusCode} latencyMs={stopwatch.ElapsedMilliseconds} apiCost='{FormatApiMetric(usage.ApiCost)}' remaining='{FormatApiMetric(usage.ApiRemaining)}' spendSoFar='{FormatApiMetric(usage.ApiSpendSoFar)}' totalAvailable='{FormatApiMetric(usage.ApiTotalAvailable)}' usageSource='{usage.Source}'");
        EmitApiCallTrace(new NdApiCallTrace
        {
            Method = "GET",
            RelativePath = relativeOrAbsolutePath,
            Url = requestUri.ToString(),
            StatusCode = (int)response.StatusCode,
            Succeeded = true,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ResponseLength = content.Length,
            ResponsePreview = BuildResponsePreview(content),
            ApiCost = usage.ApiCost,
            ApiRemaining = usage.ApiRemaining,
            ApiSpendSoFar = usage.ApiSpendSoFar,
            ApiTotalAvailable = usage.ApiTotalAvailable,
            ApiUsageSource = usage.Source
        });

        return JsonDocument.Parse(content);
    }

    public IDisposable PushApiCallTraceObserver(Action<NdApiCallTrace> observer)
    {
        var previous = _callTraceObserver.Value;
        _callTraceObserver.Value = observer;
        return new DelegateDisposable(() => _callTraceObserver.Value = previous);
    }

    /// <summary>
    /// Executes a GET request and deserializes JSON into a typed model.
    /// </summary>
    /// <typeparam name="T">Target CLR type for deserialization.</typeparam>
    /// <param name="relativeOrAbsolutePath">Relative API path or absolute URL.</param>
    /// <param name="cancellationToken">Token used to cancel the HTTP call.</param>
    /// <returns>The deserialized model instance, or <see langword="null"/> when payload is empty.</returns>
    public async Task<T?> GetJsonAsync<T>(string relativeOrAbsolutePath, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(relativeOrAbsolutePath, cancellationToken);
        return document.RootElement.Deserialize<T>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    public async Task<NdBinaryDownloadResponse> DownloadBinaryAsync(
        string relativeOrAbsolutePath,
        Stream destination,
        CancellationToken cancellationToken = default,
        TimeSpan? requestTimeout = null)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        var requestUri = BuildUri(relativeOrAbsolutePath);
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        using var response = await SendRequestHeadersReadAsync(request, cancellationToken, requestTimeout);
        stopwatch.Stop();

        var statusCode = (int)response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.Delta;
        var contentLength = response.Content.Headers.ContentLength;
        var usage = ExtractApiUsage(response, body: null);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadContentAsStringAsync(response.Content, cancellationToken);
            LogHttpFailure("GET", relativeOrAbsolutePath, requestUri, response, errorBody);
            return new NdBinaryDownloadResponse
            {
                Succeeded = false,
                StatusCode = statusCode,
                RequestPath = relativeOrAbsolutePath,
                ErrorMessage = $"{statusCode} {response.ReasonPhrase}",
                RetryAfter = retryAfter,
                ContentLength = contentLength
            };
        }

        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await sourceStream.CopyToAsync(destination, cancellationToken);

        var bytesWritten = 0L;
        if (destination.CanSeek)
        {
            bytesWritten = destination.Position;
        }
        else if (contentLength.HasValue)
        {
            bytesWritten = contentLength.Value;
        }

        Trace.WriteLine(
            $"ND-HTTP success method=GET-BINARY path='{relativeOrAbsolutePath}' url='{requestUri}' status={statusCode} latencyMs={stopwatch.ElapsedMilliseconds} bytes={bytesWritten} apiCost='{FormatApiMetric(usage.ApiCost)}' remaining='{FormatApiMetric(usage.ApiRemaining)}' spendSoFar='{FormatApiMetric(usage.ApiSpendSoFar)}' totalAvailable='{FormatApiMetric(usage.ApiTotalAvailable)}' usageSource='{usage.Source}'");

        return new NdBinaryDownloadResponse
        {
            Succeeded = true,
            StatusCode = statusCode,
            RequestPath = relativeOrAbsolutePath,
            BytesWritten = bytesWritten,
            ContentLength = contentLength
        };
    }

    /// <summary>
    /// Executes a POST request and requires a success status code.
    /// </summary>
    /// <param name="relativeOrAbsolutePath">Relative API path or absolute URL.</param>
    /// <param name="content">HTTP payload to send.</param>
    /// <param name="cancellationToken">Token used to cancel the HTTP call.</param>
    /// <param name="retryOnThrottle"><see langword="true"/> to retry transient/throttle responses.</param>
    /// <returns>A task that completes when the request succeeds.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the request fails.</exception>
    public async Task PostAsync(
        string relativeOrAbsolutePath,
        HttpContent content,
        CancellationToken cancellationToken = default,
        bool retryOnThrottle = true,
        TimeSpan? requestTimeout = null)
    {
        var requestUri = BuildUri(relativeOrAbsolutePath);
        var stopwatch = Stopwatch.StartNew();
        using var response = retryOnThrottle
            ? await SendWithRetryAsync(
                await BuildBufferedRequestFactoryAsync(HttpMethod.Post, requestUri, content, null, cancellationToken),
                cancellationToken,
                requestTimeout)
            : await SendOnceAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
                {
                    Content = content
                };
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                return request;
            }, cancellationToken, requestTimeout);
        stopwatch.Stop();
        var responseContent = await ReadContentAsStringAsync(response.Content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            LogHttpFailure("POST", relativeOrAbsolutePath, requestUri, response, responseContent);
            throw new InvalidOperationException(
                $"NetDocuments API request failed ({(int)response.StatusCode} {response.ReasonPhrase}) for '{relativeOrAbsolutePath}'. Snippet: {BuildSnippet(responseContent)}");
        }

        var usage = ExtractApiUsage(response, responseContent);
        Trace.WriteLine(
            $"ND-HTTP success method=POST path='{relativeOrAbsolutePath}' url='{requestUri}' status={(int)response.StatusCode} latencyMs={stopwatch.ElapsedMilliseconds} apiCost='{FormatApiMetric(usage.ApiCost)}' remaining='{FormatApiMetric(usage.ApiRemaining)}' spendSoFar='{FormatApiMetric(usage.ApiSpendSoFar)}' totalAvailable='{FormatApiMetric(usage.ApiTotalAvailable)}' usageSource='{usage.Source}'");
    }

    /// <summary>
    /// Executes a POST request and parses a JSON response when present.
    /// </summary>
    /// <param name="relativeOrAbsolutePath">Relative API path or absolute URL.</param>
    /// <param name="content">HTTP payload to send.</param>
    /// <param name="cancellationToken">Token used to cancel the HTTP call.</param>
    /// <param name="retryOnThrottle"><see langword="true"/> to retry transient/throttle responses.</param>
    /// <returns>Parsed JSON response, or <see langword="null"/> when no JSON payload is returned.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the request fails.</exception>
    public async Task<JsonDocument?> PostJsonAsync(
        string relativeOrAbsolutePath,
        HttpContent content,
        CancellationToken cancellationToken = default,
        bool retryOnThrottle = true)
    {
        var requestUri = BuildUri(relativeOrAbsolutePath);
        var stopwatch = Stopwatch.StartNew();
        using var response = retryOnThrottle
            ? await SendWithRetryAsync(await BuildBufferedRequestFactoryAsync(HttpMethod.Post, requestUri, content, null, cancellationToken), cancellationToken)
            : await SendOnceAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
                {
                    Content = content
                };
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                return request;
            }, cancellationToken);
        stopwatch.Stop();
        var responseContent = await ReadContentAsStringAsync(response.Content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            LogHttpFailure("POST", relativeOrAbsolutePath, requestUri, response, responseContent);
            throw new InvalidOperationException(
                $"NetDocuments API request failed ({(int)response.StatusCode} {response.ReasonPhrase}) for '{relativeOrAbsolutePath}'. Snippet: {BuildSnippet(responseContent)}");
        }

        var usage = ExtractApiUsage(response, responseContent);
        Trace.WriteLine(
            $"ND-HTTP success method=POST path='{relativeOrAbsolutePath}' url='{requestUri}' status={(int)response.StatusCode} latencyMs={stopwatch.ElapsedMilliseconds} apiCost='{FormatApiMetric(usage.ApiCost)}' remaining='{FormatApiMetric(usage.ApiRemaining)}' spendSoFar='{FormatApiMetric(usage.ApiSpendSoFar)}' totalAvailable='{FormatApiMetric(usage.ApiTotalAvailable)}' usageSource='{usage.Source}'");

        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return null;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return JsonDocument.Parse(responseContent);
    }

    /// <summary>
    /// Executes a POST request and returns the raw response body as text.
    /// </summary>
    /// <param name="relativeOrAbsolutePath">Relative API path or absolute URL.</param>
    /// <param name="content">HTTP payload to send.</param>
    /// <param name="cancellationToken">Token used to cancel the HTTP call.</param>
    /// <param name="retryOnThrottle"><see langword="true"/> to retry transient/throttle responses.</param>
    /// <param name="requestHeaders">Optional additional request headers.</param>
    /// <param name="requestTimeout">Optional per-request timeout override.</param>
    /// <returns>Raw response body.</returns>
    public Task<string> PostForStringAsync(
        string relativeOrAbsolutePath,
        HttpContent content,
        CancellationToken cancellationToken = default,
        bool retryOnThrottle = true,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        TimeSpan? requestTimeout = null)
    {
        return SendForStringAsync(
            HttpMethod.Post,
            relativeOrAbsolutePath,
            content,
            cancellationToken,
            retryOnThrottle,
            requestHeaders,
            requestTimeout);
    }

    /// <summary>
    /// Executes a PUT request and returns the raw response body as text.
    /// </summary>
    /// <param name="relativeOrAbsolutePath">Relative API path or absolute URL.</param>
    /// <param name="content">HTTP payload to send.</param>
    /// <param name="cancellationToken">Token used to cancel the HTTP call.</param>
    /// <param name="retryOnThrottle"><see langword="true"/> to retry transient/throttle responses.</param>
    /// <param name="requestHeaders">Optional additional request headers.</param>
    /// <param name="requestTimeout">Optional per-request timeout override.</param>
    /// <returns>Raw response body.</returns>
    public Task<string> PutForStringAsync(
        string relativeOrAbsolutePath,
        HttpContent content,
        CancellationToken cancellationToken = default,
        bool retryOnThrottle = true,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        TimeSpan? requestTimeout = null)
    {
        return SendForStringAsync(
            HttpMethod.Put,
            relativeOrAbsolutePath,
            content,
            cancellationToken,
            retryOnThrottle,
            requestHeaders,
            requestTimeout);
    }

    private async Task<string> SendForStringAsync(
        HttpMethod method,
        string relativeOrAbsolutePath,
        HttpContent content,
        CancellationToken cancellationToken,
        bool retryOnThrottle,
        IReadOnlyDictionary<string, string>? requestHeaders,
        TimeSpan? requestTimeout)
    {
        var requestUri = BuildUri(relativeOrAbsolutePath);
        var stopwatch = Stopwatch.StartNew();
        using var response = retryOnThrottle
            ? await SendWithRetryAsync(
                await BuildBufferedRequestFactoryAsync(method, requestUri, content, requestHeaders, cancellationToken),
                cancellationToken,
                requestTimeout)
            : await SendOnceAsync(() =>
            {
                var request = new HttpRequestMessage(method, requestUri)
                {
                    Content = content
                };
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                AddRequestHeaders(request, requestHeaders);
                return request;
            }, cancellationToken, requestTimeout);
        stopwatch.Stop();
        var responseContent = await ReadContentAsStringAsync(response.Content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            LogHttpFailure(method.Method, relativeOrAbsolutePath, requestUri, response, responseContent);
            throw new InvalidOperationException(
                $"NetDocuments API request failed ({(int)response.StatusCode} {response.ReasonPhrase}) for '{relativeOrAbsolutePath}'. Snippet: {BuildSnippet(responseContent)}");
        }

        var usage = ExtractApiUsage(response, responseContent);
        Trace.WriteLine(
            $"ND-HTTP success method={method.Method} path='{relativeOrAbsolutePath}' url='{requestUri}' status={(int)response.StatusCode} latencyMs={stopwatch.ElapsedMilliseconds} apiCost='{FormatApiMetric(usage.ApiCost)}' remaining='{FormatApiMetric(usage.ApiRemaining)}' spendSoFar='{FormatApiMetric(usage.ApiSpendSoFar)}' totalAvailable='{FormatApiMetric(usage.ApiTotalAvailable)}' usageSource='{usage.Source}'");

        return responseContent;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        const int maxAttempts = 4;
        var delay = TimeSpan.FromMilliseconds(500);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var request = requestFactory();
            var response = await SendRequestAsync(request, cancellationToken, requestTimeout);
            if (!ShouldRetry(response.StatusCode) || attempt == maxAttempts)
            {
                return response;
            }

            var retryAfter = response.Headers.RetryAfter?.Delta ?? delay;
            var usage = ExtractApiUsage(response, body: null);
            Trace.WriteLine(
                $"ND-HTTP retry status={(int)response.StatusCode} attempt={attempt}/{maxAttempts} retryAfterMs={retryAfter.TotalMilliseconds:F0} apiCost='{FormatApiMetric(usage.ApiCost)}' remaining='{FormatApiMetric(usage.ApiRemaining)}' spendSoFar='{FormatApiMetric(usage.ApiSpendSoFar)}' totalAvailable='{FormatApiMetric(usage.ApiTotalAvailable)}' usageSource='{usage.Source}'");
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
        var usage = ExtractApiUsage(response, body);
        Trace.WriteLine(
            $"ND-HTTP error method={method} path='{path}' url='{requestUri}' status={(int)response.StatusCode} reason='{response.ReasonPhrase}' mediaType='{mediaType}' apiCost='{FormatApiMetric(usage.ApiCost)}' remaining='{FormatApiMetric(usage.ApiRemaining)}' spendSoFar='{FormatApiMetric(usage.ApiSpendSoFar)}' totalAvailable='{FormatApiMetric(usage.ApiTotalAvailable)}' usageSource='{usage.Source}' snippet='{snippet}'");
    }

    private static string BuildSnippet(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(text);
        if (LooksLikeAkamaiAccessDenied(decoded))
        {
            var akamaiSnippet = BuildAkamaiSnippet(decoded);
            return SensitiveDataRedactor.RedactBearerTokens(akamaiSnippet);
        }

        var normalized = NormalizeWhitespace(decoded);
        var snippet = normalized.Length > 360 ? normalized[..360] : normalized;
        return SensitiveDataRedactor.RedactBearerTokens(snippet);
    }

    private static string BuildResponsePreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = NormalizeWhitespace(text);
        if (normalized.Length > 2048)
        {
            normalized = normalized[..2048];
        }

        return SensitiveDataRedactor.RedactBearerTokens(normalized);
    }

    private static bool LooksLikeAkamaiAccessDenied(string text)
    {
        return text.IndexOf("Access Denied", StringComparison.OrdinalIgnoreCase) >= 0 &&
               text.IndexOf("permission to access", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildAkamaiSnippet(string htmlOrText)
    {
        var plainText = NormalizeWhitespace(HtmlTagRegex.Replace(htmlOrText, " "));
        var urlMatch = UrlRegex.Match(plainText);
        var url = urlMatch.Success ? urlMatch.Value : string.Empty;
        var reference = TryExtractReferenceToken(plainText);

        var snippet = "Akamai WAF Access Denied";
        if (!string.IsNullOrWhiteSpace(url))
        {
            snippet += $" url='{url}'";
        }

        if (!string.IsNullOrWhiteSpace(reference))
        {
            snippet += $" reference='{reference}'";
        }

        if (snippet.Length > 360)
        {
            snippet = snippet[..360];
        }

        return snippet;
    }

    private static string TryExtractReferenceToken(string text)
    {
        var match = ReferenceRegex.Match(text);
        if (!match.Success)
        {
            return string.Empty;
        }

        var value = match.Groups["value"].Value
            .Trim()
            .Trim('"', '\'', '.', ',', ';', ':', ')', '(');

        if (value.EndsWith("&", StringComparison.Ordinal))
        {
            value = value[..^1];
        }

        return value;
    }

    private static string NormalizeWhitespace(string text)
    {
        return WhitespaceRegex.Replace(text, " ").Trim();
    }

    private static async Task<string> ReadContentAsStringAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return string.Empty;
        }

        var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var encodings = content.Headers.ContentEncoding
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();

        if (encodings.Count == 0 && LooksLikeGzip(bytes))
        {
            encodings.Add("gzip");
        }

        if (encodings.Count > 0)
        {
            try
            {
                using var compressedStream = new MemoryStream(bytes, writable: false);
                using var decodedStream = WrapDecodeStream(compressedStream, encodings);
                using var reader = new StreamReader(
                    decodedStream,
                    ResolveTextEncoding(content.Headers.ContentType?.CharSet),
                    detectEncodingFromByteOrderMarks: true);
                return await reader.ReadToEndAsync();
            }
            catch (InvalidDataException)
            {
                // Fall back to decoding raw bytes below.
            }
            catch (NotSupportedException)
            {
                // Fall back to decoding raw bytes below.
            }
        }

        return DecodeBytes(bytes, content.Headers.ContentType?.CharSet);
    }

    private static Stream WrapDecodeStream(Stream baseStream, IReadOnlyList<string> encodings)
    {
        Stream stream = baseStream;
        for (var i = encodings.Count - 1; i >= 0; i--)
        {
            stream = encodings[i].ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(stream, CompressionMode.Decompress, leaveOpen: false),
                "deflate" => new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: false),
                "br" => new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: false),
                _ => stream
            };
        }

        return stream;
    }

    private static bool LooksLikeGzip(byte[] bytes)
    {
        return bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
    }

    private static string DecodeBytes(byte[] bytes, string? charset)
    {
        var encoding = ResolveTextEncoding(charset);
        return encoding.GetString(bytes);
    }

    private static Encoding ResolveTextEncoding(string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // Ignore invalid charset and fall back to UTF-8.
            }
        }

        return Encoding.UTF8;
    }

    private static async Task<Func<HttpRequestMessage>> BuildBufferedRequestFactoryAsync(
        HttpMethod method,
        Uri requestUri,
        HttpContent content,
        IReadOnlyDictionary<string, string>? requestHeaders,
        CancellationToken cancellationToken)
    {
        var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
        var headers = content.Headers
            .Select(header => new KeyValuePair<string, IReadOnlyList<string>>(header.Key, header.Value.ToList()))
            .ToList();

        return () =>
        {
            var request = new HttpRequestMessage(method, requestUri);
            var requestContent = new ByteArrayContent(bytes);
            foreach (var header in headers)
            {
                requestContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            request.Content = requestContent;
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            AddRequestHeaders(request, requestHeaders);
            return request;
        };
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        var request = requestFactory();
        return await SendRequestAsync(request, cancellationToken, requestTimeout);
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout)
    {
        if (requestTimeout is null || requestTimeout <= TimeSpan.Zero)
        {
            return await _client.SendAsync(request, cancellationToken);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(requestTimeout.Value);
        return await _client.SendAsync(request, timeoutCts.Token);
    }

    private async Task<HttpResponseMessage> SendRequestHeadersReadAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout)
    {
        if (requestTimeout is null || requestTimeout <= TimeSpan.Zero)
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(requestTimeout.Value);
        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
    }

    private static void AddRequestHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string>? requestHeaders)
    {
        if (requestHeaders is null)
        {
            return;
        }

        foreach (var header in requestHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode == (HttpStatusCode)429 ||
               statusCode == HttpStatusCode.RequestTimeout ||
               statusCode == HttpStatusCode.InternalServerError ||
               statusCode == HttpStatusCode.BadGateway ||
               statusCode == HttpStatusCode.ServiceUnavailable ||
               statusCode == HttpStatusCode.GatewayTimeout;
    }

    private static string? TryReadFirstHeaderValue(HttpResponseMessage response, string headerName)
    {
        if (!response.Headers.TryGetValues(headerName, out var values))
        {
            return null;
        }

        return values.FirstOrDefault();
    }

    private static NdApiUsageSnapshot ExtractApiUsage(HttpResponseMessage response, string? body)
    {
        decimal? apiCost = null;
        foreach (var headerName in ApiCostHeaderNames)
        {
            if (TryReadHeaderDecimal(response, headerName, out apiCost))
            {
                break;
            }
        }

        decimal? apiRemaining = null;
        foreach (var headerName in ApiRemainingHeaderNames)
        {
            if (TryReadHeaderDecimal(response, headerName, out apiRemaining))
            {
                break;
            }
        }

        var sourceParts = new List<string>();
        if (apiCost.HasValue || apiRemaining.HasValue)
        {
            sourceParts.Add("headers");
        }

        decimal? totalAvailable = null;
        decimal? spendSoFar = null;
        if (TryExtractApiUsageFromBody(body, out var bodyTotalAvailable, out var bodySpendSoFar))
        {
            totalAvailable = bodyTotalAvailable;
            spendSoFar = bodySpendSoFar;
            if (totalAvailable.HasValue || spendSoFar.HasValue)
            {
                sourceParts.Add("body");
            }
        }

        return new NdApiUsageSnapshot(
            apiCost,
            apiRemaining,
            spendSoFar,
            totalAvailable,
            string.Join("+", sourceParts));
    }

    private static bool TryReadHeaderDecimal(HttpResponseMessage response, string headerName, out decimal? value)
    {
        value = null;
        var raw = TryReadFirstHeaderValue(response, headerName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryExtractApiUsageFromBody(string? body, out decimal? totalAvailable, out decimal? spendSoFar)
    {
        totalAvailable = null;
        spendSoFar = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return TryExtractApiUsageFromElement(document.RootElement, out totalAvailable, out spendSoFar);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryExtractApiUsageFromElement(JsonElement element, out decimal? totalAvailable, out decimal? spendSoFar)
    {
        totalAvailable = null;
        spendSoFar = null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            var hasTotal = TryReadNamedDecimal(element, ApiTotalAvailablePropertyNames, out totalAvailable);
            var hasSpend = TryReadNamedDecimal(element, ApiSpendSoFarPropertyNames, out spendSoFar);
            if (hasTotal || hasSpend)
            {
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryExtractApiUsageFromElement(property.Value, out totalAvailable, out spendSoFar))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryExtractApiUsageFromElement(item, out totalAvailable, out spendSoFar))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadNamedDecimal(JsonElement element, IReadOnlyList<string> names, out decimal? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadDecimal(property.Value, out var parsed))
            {
                value = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        value = default;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDecimal(out value);
            case JsonValueKind.String:
                return decimal.TryParse(
                    element.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value);
            default:
                return false;
        }
    }

    private static string FormatApiMetric(decimal? value)
    {
        return value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private void EmitApiCallTrace(NdApiCallTrace trace)
    {
        _callTraceObserver.Value?.Invoke(trace);
    }

    private readonly record struct NdApiUsageSnapshot(
        decimal? ApiCost,
        decimal? ApiRemaining,
        decimal? ApiSpendSoFar,
        decimal? ApiTotalAvailable,
        string Source);

    private sealed class DelegateDisposable : IDisposable
    {
        private readonly Action _disposeAction;
        private bool _disposed;

        public DelegateDisposable(Action disposeAction)
        {
            _disposeAction = disposeAction;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _disposeAction();
        }
    }
}
