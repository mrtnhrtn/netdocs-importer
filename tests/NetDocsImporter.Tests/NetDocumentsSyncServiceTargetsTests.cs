using System.Net;
using System.Text;
using NetDocsImporter.Core;
using NetDocsImporter.Data;
using NetDocsImporter.NetDocs;

namespace NetDocsImporter.Tests;

public class NetDocumentsSyncServiceTargetsTests
{
    [Fact]
    public async Task GetContainerChildrenAsync_ParsesNestedStandardAttributesRows()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var requests = new List<Uri>();

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                lock (requests)
                {
                    requests.Add(request.RequestUri!);
                }

                if (request.Method != HttpMethod.Get || request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"error":"method not allowed"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=WS-1", StringComparison.OrdinalIgnoreCase))
                {
                    if (query.Contains("ndflt", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponse(HttpStatusCode.OK, """
                            {
                              "standardList": [
                                {
                                  "standardAttributes": {
                                    "id": "FLT-1",
                                    "description": "Assigned To Me",
                                    "Ext": "ndflt",
                                    "workspaceId": "WS-1"
                                  }
                                }
                              ]
                            }
                            """);
                    }

                    if (query.Contains("ndfld", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponse(HttpStatusCode.OK, """
                            {
                              "standardList": [
                                {
                                  "standardAttributes": {
                                    "id": "FLD-1",
                                    "description": "General",
                                    "Ext": "ndfld",
                                    "workspaceId": "WS-1"
                                  }
                                }
                              ]
                            }
                            """);
                    }

                    return JsonResponse(HttpStatusCode.OK, """{"standardList": []}""");
                }

                if (string.Equals(path, "/v2/search", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=WS-1", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardList": []}""");
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var nodes = await service.GetContainerChildrenAsync("NG-CAB", parentContainerId: "WS-1");

            Assert.Equal(2, nodes.Count);

            var folder = Assert.Single(nodes, node => node.Id == "FLD-1");
            Assert.Equal("General", folder.Name);
            Assert.Equal(NdTargetType.Folder, folder.SupportedType);
            Assert.True(folder.HasChildren);

            var filter = Assert.Single(nodes, node => node.Id == "FLT-1");
            Assert.Equal("Assigned To Me", filter.Name);
            Assert.Equal(NdTargetType.WorkspaceFilter, filter.SupportedType);
            Assert.False(filter.HasChildren);

            Assert.DoesNotContain(
                requests,
                uri =>
                    string.Equals(uri.AbsolutePath, "/v2/search", StringComparison.OrdinalIgnoreCase) &&
                    uri.Query.Contains("container=", StringComparison.OrdinalIgnoreCase) &&
                    !uri.Query.Contains("cabinets=", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteIfExists(dbPath);
            DeleteIfExists($"{dbPath}-wal");
            DeleteIfExists($"{dbPath}-shm");
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static NetDocumentsSyncService CreateSyncService(HttpMessageHandler handler, string dbPath)
    {
        var apiClient = new NetDocumentsApiClient(
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

        var store = new JobStore(dbPath);
        return new NetDocumentsSyncService(apiClient, store);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string payload)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
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
