using System.Net;
using System.Text;
using NetDocsImporter.Core;
using NetDocsImporter.Data;
using NetDocsImporter.NetDocs;

namespace NetDocsImporter.Tests;

public sealed class NetDocumentsSyncServiceExportTests
{
    [Fact]
    public async Task EnumerateExportScopesAsync_RespectsWorkspaceFilterInclusionToggle()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-scope-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.BadRequest, """{"error":"missing uri"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=ROOT", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("extension eq 'ndfld'", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "standardList": [
                            {
                              "standardAttributes": {
                                "id": "FLD-1",
                                "description": "General",
                                "Ext": "ndfld",
                                "workspaceId": "ROOT"
                              }
                            }
                          ]
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=ROOT", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("extension eq 'ndflt'", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "standardList": [
                            {
                              "standardAttributes": {
                                "id": "FLT-1",
                                "description": "Assigned To Me",
                                "Ext": "ndflt",
                                "workspaceId": "ROOT"
                              }
                            }
                          ]
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardList": []}""");
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var root = new NdTargetSelection
            {
                Type = NdTargetType.Workspace,
                Id = "ROOT",
                Name = "Workspace Root",
                Extension = "ndws"
            };

            var includeFilters = await service.EnumerateExportScopesAsync("NG-CAB", root, includeWorkspaceFilters: true);
            var excludeFilters = await service.EnumerateExportScopesAsync("NG-CAB", root, includeWorkspaceFilters: false);

            Assert.Equal(3, includeFilters.Count);
            Assert.Contains(includeFilters, scope => scope.Kind == NdExportScopeKind.WorkspaceFilter);

            Assert.Equal(2, excludeFilters.Count);
            Assert.DoesNotContain(excludeFilters, scope => scope.Kind == NdExportScopeKind.WorkspaceFilter);
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
    public async Task EnumerateContainerDocumentsAsync_ReadsPagedDocumentsAndAttributes()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-doc-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.BadRequest, """{"error":"missing uri"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=ROOT", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("skip=0", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "standardList": [
                            {
                              "standardAttributes": {
                                "docId": "DOC-1",
                                "description": "Project Plan.docx",
                                "sizeBytes": 1000
                              },
                              "versionsLite": {
                                "officialVersionId": "2"
                              },
                              "customAttributes": {
                                "1001": "Matter-1"
                              }
                            },
                            {
                              "standardAttributes": {
                                "docId": "DOC-2",
                                "description": "Cover Letter.pdf",
                                "sizeBytes": 2000
                              },
                              "versionsLite": {
                                "officialVersionId": "1"
                              }
                            }
                          ]
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=ROOT", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("skip=200", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardList": []}""");
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var scope = new NdExportScope
            {
                ContainerId = "ROOT",
                Name = "Workspace Root",
                TargetType = NdTargetType.Workspace,
                Kind = NdExportScopeKind.Workspace
            };

            var documents = await service.EnumerateContainerDocumentsAsync("NG-CAB", scope, customAttributeIds: new[] { "1001" });

            Assert.Equal(2, documents.Count);

            var first = Assert.Single(documents, document => document.DocumentId == "DOC-1");
            Assert.Equal("Project Plan.docx", first.FileName);
            Assert.Equal(1000, first.SizeBytes);
            Assert.Equal("2", first.OfficialVersionId);
            Assert.Contains(first.CustomAttributes, attribute => attribute.Name == "custom.1001" && attribute.Value == "Matter-1");
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
    public async Task EnumerateDocumentVersionsAsync_ParsesVersionRows()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-version-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.BadRequest, """{"error":"missing uri"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                if (string.Equals(path, "/v2/document/DOC-1/version", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": [
                            {
                              "versionId": "1",
                              "description": "Project Plan v1.docx",
                              "sizeBytes": 1000,
                              "official": false
                            },
                            {
                              "versionId": "2",
                              "description": "Project Plan v2.docx",
                              "sizeBytes": 1250,
                              "official": true
                            }
                          ]
                        }
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var versions = await service.EnumerateDocumentVersionsAsync("DOC-1");

            Assert.Equal(2, versions.Count);
            Assert.Contains(versions, version => version.VersionId == "2" && version.IsOfficial);
            Assert.Contains(versions, version => version.VersionId == "1" && version.SizeBytes == 1000);
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
