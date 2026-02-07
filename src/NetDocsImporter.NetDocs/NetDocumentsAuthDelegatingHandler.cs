using System.Net;
using System.Net.Http.Headers;

namespace NetDocsImporter.NetDocs;

internal sealed class NetDocumentsAuthDelegatingHandler : DelegatingHandler
{
    private readonly INetDocumentsAuthService _authService;
    private readonly Func<NetDocumentsAuthContext> _contextAccessor;

    public NetDocumentsAuthDelegatingHandler(
        INetDocumentsAuthService authService,
        Func<NetDocumentsAuthContext> contextAccessor,
        HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _contextAccessor = contextAccessor ?? throw new ArgumentNullException(nameof(contextAccessor));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = _contextAccessor();
        var accessToken = await _authService.GetAccessTokenAsync(context, false, cancellationToken);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        var retryRequest = await CloneRequestAsync(request, cancellationToken);
        var refreshedToken = await _authService.GetAccessTokenAsync(context, true, cancellationToken);
        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken);
        return await base.SendAsync(retryRequest, cancellationToken);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var stream = await request.Content.ReadAsStreamAsync(cancellationToken);
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;

            clone.Content = new StreamContent(memory);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        clone.Version = request.Version;
        clone.VersionPolicy = request.VersionPolicy;
        return clone;
    }
}
