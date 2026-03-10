using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NetDocsImporter.NetDocs;

namespace NetDocsImporter.Tests;

public sealed class NetDocumentsApiClientTests
{
    [Fact]
    public async Task GetJsonAsync_CapturesApiUsageFromResponseBody()
    {
        var handler = new StubHttpHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            {
              "data": [],
              "apiUsage": {
                "totalAvailable": 1000,
                "spendSoFar": 25
              }
            }
            """));

        var client = CreateClient(handler);
        NdApiCallTrace? trace = null;

        using (client.PushApiCallTraceObserver(item => trace = item))
        {
            using var _ = await client.GetJsonAsync("/v2/test");
        }

        Assert.NotNull(trace);
        Assert.Equal(1000m, trace!.ApiTotalAvailable);
        Assert.Equal(25m, trace.ApiSpendSoFar);
        Assert.Equal("body", trace.ApiUsageSource);
        Assert.Null(trace.ApiCost);
        Assert.Null(trace.ApiRemaining);
    }

    [Fact]
    public async Task GetJsonAsync_CapturesApiUsageFromHeaders()
    {
        var response = JsonResponse(HttpStatusCode.OK, """{"data":[]}""");
        response.Headers.TryAddWithoutValidation("X-ND-API-Cost", "3");
        response.Headers.TryAddWithoutValidation("X-ND-API-Remaining", "97");
        var handler = new StubHttpHandler(_ => response);

        var client = CreateClient(handler);
        NdApiCallTrace? trace = null;

        using (client.PushApiCallTraceObserver(item => trace = item))
        {
            using var _ = await client.GetJsonAsync("/v2/test");
        }

        Assert.NotNull(trace);
        Assert.Equal(3m, trace!.ApiCost);
        Assert.Equal(97m, trace.ApiRemaining);
        Assert.Equal("headers", trace.ApiUsageSource);
        Assert.Null(trace.ApiSpendSoFar);
        Assert.Null(trace.ApiTotalAvailable);
    }

    private static NetDocumentsApiClient CreateClient(HttpMessageHandler handler)
    {
        return new NetDocumentsApiClient(
            new StubAuthService(),
            () => new NetDocumentsAuthContext
            {
                OAuthAuthorizeBaseUrl = "https://auth.example.com",
                OAuthTokenUrl = "https://auth.example.com/token",
                ClientId = "client-id",
                ClientSecret = "client-secret",
                RedirectUri = "http://127.0.0.1:5000/callback"
            },
            () => "https://api.au.netdocuments.com",
            handler);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string payload)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubAuthService : INetDocumentsAuthService
    {
        public Task SignInInteractiveAsync(NetDocumentsAuthContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<string> GetAccessTokenAsync(
            NetDocumentsAuthContext context,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult("test-token");
        }

        public Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
