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

    [Fact]
    public async Task GetContainerChildrenAsync_ResolvesContainerScopeBeforeSearchingChildren()
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
                if (string.Equals(path, "/v2/container/3437-5615-8479/info", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": {
                            "id": ":AU2:o:w:m:v:^W200423132232851.nev",
                            "description": "Demo Workspace",
                            "extension": "ndws"
                          }
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=:AU2:o:w:m:v:^W200423132232851.nev", StringComparison.OrdinalIgnoreCase))
                {
                    if (query.Contains("ndfld", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponse(HttpStatusCode.OK, """
                            {
                              "standardList": [
                                {
                                  "standardAttributes": {
                                    "id": "FLD-200",
                                    "description": "Correspondence",
                                    "Ext": "ndfld",
                                    "workspaceId": ":AU2:o:w:m:v:^W200423132232851.nev"
                                  }
                                }
                              ]
                            }
                            """);
                    }

                    return JsonResponse(HttpStatusCode.OK, """{"standardList": []}""");
                }

                if (string.Equals(path, "/v2/search", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardList": []}""");
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var nodes = await service.GetContainerChildrenAsync(
                "NG-CAB",
                parentContainerId: "3437-5615-8479",
                workspaceId: "3437-5615-8479");

            var folder = Assert.Single(nodes);
            Assert.Equal("FLD-200", folder.Id);
            Assert.Equal(NdTargetType.Folder, folder.SupportedType);

            Assert.Contains(
                requests,
                uri => string.Equals(uri.AbsolutePath, "/v2/container/3437-5615-8479/info", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                requests,
                uri => string.Equals(uri.AbsolutePath, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                       Uri.UnescapeDataString(uri.Query).Contains("container=:AU2:o:w:m:v:^W200423132232851.nev", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public async Task GetCabinetTopLevelFoldersAsync_UsesV2AndSkipsFilters()
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

                if (string.Equals(request.RequestUri.AbsolutePath, "/v2/cabinet/NG-CAB/folders", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "standardList": [
                            {
                              "standardAttributes": {
                                "id": "ROOT-WS",
                                "description": "Top Workspace",
                                "Ext": "ndws"
                              }
                            },
                            {
                              "standardAttributes": {
                                "id": "ROOT-FOLDER",
                                "description": "Top Folder",
                                "Ext": "ndfld"
                              }
                            },
                            {
                              "standardAttributes": {
                                "id": "ROOT-FILTER",
                                "description": "Top Filter",
                                "Ext": "ndflt"
                              }
                            }
                          ]
                        }
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var nodes = await service.GetCabinetTopLevelFoldersAsync("NG-CAB");

            Assert.Equal(2, nodes.Count);
            Assert.Contains(nodes, n => n.Id == "ROOT-WS" && n.SupportedType == NdTargetType.Workspace);
            Assert.Contains(nodes, n => n.Id == "ROOT-FOLDER" && n.SupportedType == NdTargetType.Folder);
            Assert.DoesNotContain(nodes, n => n.SupportedType == NdTargetType.WorkspaceFilter);
            Assert.Contains(
                requests,
                uri => string.Equals(uri.AbsolutePath, "/v2/cabinet/NG-CAB/folders", StringComparison.OrdinalIgnoreCase));
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
