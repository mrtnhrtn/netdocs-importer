using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetDocsImporter.NetDocs;

/// <summary>
/// Implements OAuth authorization-code and refresh-token flows for NetDocuments desktop authentication.
/// </summary>
public sealed class NetDocumentsAuthService : INetDocumentsAuthService
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly HttpClient _httpClient;
    private readonly NetDocumentsTokenStore _tokenStore;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private NetDocumentsTokenCache? _cache;

    /// <summary>
    /// Initializes a new authentication service backed by an encrypted token cache file.
    /// </summary>
    /// <param name="tokenPath">Path to the local token cache file.</param>
    /// <param name="httpClient">Optional HTTP client override used primarily by tests.</param>
    public NetDocumentsAuthService(string tokenPath, HttpClient? httpClient = null)
    {
        _tokenStore = new NetDocumentsTokenStore(tokenPath);
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Runs an interactive OAuth sign-in flow and persists the returned access and refresh tokens.
    /// </summary>
    /// <param name="context">OAuth client settings for the selected region.</param>
    /// <param name="cancellationToken">Token used to cancel browser/listener operations.</param>
    /// <returns>A task that completes when sign-in succeeds and tokens are saved.</returns>
    /// <exception cref="InvalidOperationException">Thrown for invalid context, callback failures, or token exchange failures.</exception>
    public async Task SignInInteractiveAsync(NetDocumentsAuthContext context, CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        var activeContext = NormalizeContext(context);

        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        HttpListener? listener = null;
        try
        {
            Trace.WriteLine($"ND-AUTH listener bind attempt redirectUri='{activeContext.RedirectUri}'.");
            listener = CreateListener(activeContext.RedirectUri);
            listener.Start();
            Trace.WriteLine($"ND-AUTH listener bind success redirectUri='{activeContext.RedirectUri}'.");
        }
        catch (HttpListenerException ex)
        {
            Trace.WriteLine($"ND-AUTH listener bind failed redirectUri='{activeContext.RedirectUri}' error={ex.ErrorCode} message='{ex.Message}'.");
            throw new InvalidOperationException(
                $"Failed to listen on redirect URI '{activeContext.RedirectUri}'. The prefix is unavailable on this machine (error={ex.ErrorCode}). " +
                "Use a registered loopback redirect URI that is free (for example localhost if registered), or free the conflicting URL reservation.",
                ex);
        }

        using (listener)
        {
            var authorizeUrl = BuildAuthorizeUrl(activeContext, state);
            Trace.WriteLine($"ND-AUTH browser launch authorizeUrl='{authorizeUrl}'.");
            OpenSystemBrowser(authorizeUrl);
            var callback = await WaitForCallbackAsync(listener, cancellationToken);

            try
            {
                await SendBrowserResponseAsync(callback.Context, callback.ErrorMessage, cancellationToken);
            }
            catch
            {
                // Ignore callback response write failures.
            }

            if (!string.IsNullOrWhiteSpace(callback.ErrorMessage))
            {
                throw new InvalidOperationException(callback.ErrorMessage);
            }

            if (!string.Equals(callback.State, state, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("OAuth state validation failed.");
            }

            if (string.IsNullOrWhiteSpace(callback.Code))
            {
                throw new InvalidOperationException("Authorization code was not returned.");
            }

            var token = await ExchangeCodeForTokenAsync(activeContext, callback.Code, cancellationToken);
            await SaveCacheAsync(token, cancellationToken);
        }
    }

    private static NetDocumentsAuthContext NormalizeContext(NetDocumentsAuthContext context)
    {
        return new NetDocumentsAuthContext
        {
            OAuthAuthorizeBaseUrl = context.OAuthAuthorizeBaseUrl?.Trim() ?? string.Empty,
            OAuthTokenUrl = context.OAuthTokenUrl?.Trim() ?? string.Empty,
            ClientId = context.ClientId?.Trim() ?? string.Empty,
            ClientSecret = context.ClientSecret ?? string.Empty,
            RedirectUri = context.RedirectUri?.Trim() ?? string.Empty
        };
    }

    /// <summary>
    /// Gets a valid access token from cache, refreshing tokens when required.
    /// </summary>
    /// <param name="context">OAuth client settings for the selected region.</param>
    /// <param name="forceRefresh"><see langword="true"/> to force refresh even when current token is still valid.</param>
    /// <param name="cancellationToken">Token used to cancel token acquisition.</param>
    /// <returns>A non-empty access token string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no session exists, refresh token is unavailable, or refresh fails.</exception>
    public async Task<string> GetAccessTokenAsync(
        NetDocumentsAuthContext context,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            _cache ??= await _tokenStore.ReadAsync(cancellationToken);
            if (_cache is null)
            {
                throw new InvalidOperationException("Not connected to NetDocuments.");
            }

            if (!forceRefresh && HasUsableAccessToken(_cache))
            {
                return _cache.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(_cache.RefreshToken))
            {
                throw new InvalidOperationException("No refresh token is available. Please connect again.");
            }

            var refreshed = await RefreshTokenAsync(context, _cache.RefreshToken, cancellationToken);
            await SaveCacheAsync(refreshed, cancellationToken);
            return refreshed.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Clears in-memory and persisted token state for the current user.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel sign-out persistence operations.</param>
    /// <returns>A task that completes when local token state is removed.</returns>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            _cache = null;
            await _tokenStore.DeleteAsync(cancellationToken);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string BuildAuthorizeUrl(NetDocumentsAuthContext context, string state)
    {
        var authorizeUrl = context.OAuthAuthorizeBaseUrl.Trim();
        if (!authorizeUrl.Contains("?", StringComparison.Ordinal) &&
            !authorizeUrl.EndsWith("OAuth.aspx", StringComparison.OrdinalIgnoreCase) &&
            !authorizeUrl.EndsWith("/oauth/authorize", StringComparison.OrdinalIgnoreCase))
        {
            authorizeUrl = $"{authorizeUrl.TrimEnd('/')}/oauth/authorize";
        }

        var query = new StringBuilder();
        query.Append("response_type=code");
        query.Append("&client_id=").Append(Uri.EscapeDataString(context.ClientId));
        query.Append("&redirect_uri=").Append(Uri.EscapeDataString(context.RedirectUri));
        query.Append("&state=").Append(Uri.EscapeDataString(state));

        var separator = authorizeUrl.Contains("?", StringComparison.Ordinal) ? "&" : "?";
        return $"{authorizeUrl}{separator}{query}";
    }

    private static HttpListener CreateListener(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect))
        {
            throw new InvalidOperationException("Redirect URI is invalid.");
        }

        var isHttp = string.Equals(redirect.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHost =
            string.Equals(redirect.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(redirect.Host, "localhost", StringComparison.OrdinalIgnoreCase);

        if (!isHttp || !isLoopbackHost)
        {
            throw new InvalidOperationException("Redirect URI must be an HTTP loopback URL on 127.0.0.1 or localhost.");
        }

        var path = redirect.AbsolutePath.Trim('/');
        var prefixPath = string.IsNullOrWhiteSpace(path) ? string.Empty : $"{path}/";
        var prefix = $"{redirect.Scheme}://{redirect.Host}:{redirect.Port}/{prefixPath}";

        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        return listener;
    }

    private static void OpenSystemBrowser(string authorizeUrl)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = authorizeUrl,
            UseShellExecute = true
        };

        Process.Start(startInfo);
    }

    private static async Task<(HttpListenerContext Context, string Code, string State, string? ErrorMessage)> WaitForCallbackAsync(
        HttpListener listener,
        CancellationToken cancellationToken)
    {
        using var ctr = cancellationToken.Register(() =>
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // Ignore.
            }
        });

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync();
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var query = context.Request.QueryString;
        var error = query["error"];
        var errorDescription = query["error_description"];
        var code = query["code"] ?? string.Empty;
        var state = query["state"] ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(error))
        {
            var message = string.IsNullOrWhiteSpace(errorDescription)
                ? $"Authorization failed: {error}."
                : $"Authorization failed: {errorDescription}.";
            return (context, code, state, message);
        }

        return (context, code, state, null);
    }

    private static async Task SendBrowserResponseAsync(
        HttpListenerContext context,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        const string success = "<html><body><h3>NetDocuments connection complete.</h3>You can return to NetDocsImporter.</body></html>";
        var body = string.IsNullOrWhiteSpace(errorMessage)
            ? success
            : $"<html><body><h3>NetDocuments connection failed.</h3>{WebUtility.HtmlEncode(errorMessage)}</body></html>";

        var data = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = string.IsNullOrWhiteSpace(errorMessage) ? 200 : 400;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = data.Length;
        await context.Response.OutputStream.WriteAsync(data.AsMemory(0, data.Length), cancellationToken);
        context.Response.OutputStream.Close();
    }

    private async Task<NetDocumentsTokenCache> ExchangeCodeForTokenAsync(
        NetDocumentsAuthContext context,
        string code,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, context.OAuthTokenUrl);
        request.Content = BuildTokenRequestContent(new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = context.RedirectUri,
            ["client_id"] = context.ClientId,
            ["client_secret"] = context.ClientSecret
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token exchange failed ({(int)response.StatusCode}).");
        }

        return ParseTokenResponse(body);
    }

    private async Task<NetDocumentsTokenCache> RefreshTokenAsync(
        NetDocumentsAuthContext context,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, context.OAuthTokenUrl);
        request.Content = BuildTokenRequestContent(new Dictionary<string, string?>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = context.ClientId,
            ["client_secret"] = context.ClientSecret
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token refresh failed ({(int)response.StatusCode}).");
        }

        var refreshed = ParseTokenResponse(body);
        if (string.IsNullOrWhiteSpace(refreshed.RefreshToken))
        {
            refreshed.RefreshToken = refreshToken;
        }

        return refreshed;
    }

    private static FormUrlEncodedContent BuildTokenRequestContent(IReadOnlyDictionary<string, string?> values)
    {
        var pairs = values
            .Where(v => !string.IsNullOrWhiteSpace(v.Value))
            .Select(v => new KeyValuePair<string, string>(v.Key, v.Value!))
            .ToList();

        return new FormUrlEncodedContent(pairs);
    }

    private static NetDocumentsTokenCache ParseTokenResponse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var accessToken = ReadString(root, "access_token");
        var refreshToken = ReadString(root, "refresh_token");
        var expiresInSeconds = ReadInt(root, "expires_in");
        if (expiresInSeconds <= 0)
        {
            expiresInSeconds = 3600;
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("OAuth response did not include an access token.");
        }

        return new NetDocumentsTokenCache
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresInSeconds)
        };
    }

    private async Task SaveCacheAsync(NetDocumentsTokenCache cache, CancellationToken cancellationToken)
    {
        _cache = cache;
        await _tokenStore.WriteAsync(cache, cancellationToken);
    }

    private static bool HasUsableAccessToken(NetDocumentsTokenCache cache)
    {
        return !string.IsNullOrWhiteSpace(cache.AccessToken) &&
            cache.ExpiresAtUtc > DateTime.UtcNow.Add(RefreshSkew);
    }

    private static void ValidateContext(NetDocumentsAuthContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (string.IsNullOrWhiteSpace(context.OAuthAuthorizeBaseUrl) ||
            string.IsNullOrWhiteSpace(context.OAuthTokenUrl) ||
            string.IsNullOrWhiteSpace(context.ClientId) ||
            string.IsNullOrWhiteSpace(context.RedirectUri))
        {
            throw new InvalidOperationException("NetDocuments OAuth settings are incomplete.");
        }
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static int ReadInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out parsed))
        {
            return parsed;
        }

        return 0;
    }
}
