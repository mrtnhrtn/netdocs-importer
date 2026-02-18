using System.Net;
using System.Text;
using NetDocsImporter.Core;
using NetDocsImporter.Data;
using NetDocsImporter.NetDocs;

namespace NetDocsImporter.Tests;

public class NetDocumentsDirectUploadServiceTests
{
    [Fact]
    public async Task BuildPlanAsync_UsesPermissiveFallbackWhenFolderListingIsAmbiguous()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var filePath = Path.Combine(sourceRoot, "client_a", "sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "sample content");

        var jobId = Guid.NewGuid().ToString("N");
        var requestPaths = new List<string>();

        try
        {
            await SeedJobWithSingleNestedFileAsync(dbPath, jobId, sourceRoot, filePath);

            var handler = new StubHttpHandler(request =>
            {
                lock (requestPaths)
                {
                    requestPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
                }

                if (request.RequestUri?.AbsolutePath == "/v1/Folder/3470-9157-8890")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"list":[{"envId":":AU2:i:e:9:8:~211201092644749.nev","type":"doc"}],"sortOrder":"name"}""");
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var result = await service.BuildPlanAsync(
                jobId,
                new NdTargetSelection
                {
                    Type = NdTargetType.Folder,
                    Id = "3470-9157-8890",
                    Name = "Top level folder"
                },
                new DirectUploadPlanContext
                {
                    JobId = jobId,
                    AllowCreateFolders = false
                },
                CancellationToken.None);

            Assert.True(result.CanUpload);
            Assert.Equal(1, result.PlannedFolderCreates);
            Assert.Single(result.Files);
            Assert.StartsWith("planned:", result.Files[0].DestinationContainerId, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(result.Issues, issue => issue.Severity == DirectUploadIssueSeverity.Error);
            Assert.Contains(result.Issues, issue => issue.Code == "FOLDER_LIST_AMBIGUOUS_PERMISSIVE");
            Assert.Contains(result.Issues, issue => issue.Code == "FOLDER_CREATE_PLANNED");
            Assert.Contains("/v1/Folder/3470-9157-8890", requestPaths);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task BuildPlanAsync_DoesNotCarryFolderListCapabilityAcrossPlans()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var filePath = Path.Combine(sourceRoot, "client_a", "invoices", "sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "sample content");

        var jobId = Guid.NewGuid().ToString("N");
        var requestPaths = new List<string>();

        try
        {
            await SeedJobWithDoubleNestedFileAsync(dbPath, jobId, sourceRoot, filePath);

            var handler = new StubHttpHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                lock (requestPaths)
                {
                    requestPaths.Add(path);
                }

                if (path == "/v1/Folder/%3Abadfolder%7C1")
                {
                    return JsonResponse(HttpStatusCode.BadRequest, """{"error":":badfolder|1 is not a folder id"}""");
                }

                if (path == "/v1/Workspace/3437-5615-8479")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"list":[{"id":"folder-client-a","name":"client_a","type":"ndfld"}]}""");
                }

                if (path == "/v1/Folder/folder-client-a")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"list":[{"id":"folder-invoices","name":"invoices","type":"ndfld"}],"sortOrder":"name"}""");
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);

            var badFolderResult = await service.BuildPlanAsync(
                jobId,
                new NdTargetSelection
                {
                    Type = NdTargetType.Folder,
                    Id = ":badfolder|1",
                    Name = "Bad Folder"
                },
                new DirectUploadPlanContext
                {
                    JobId = jobId,
                    AllowCreateFolders = false
                },
                CancellationToken.None);

            Assert.False(badFolderResult.CanUpload);
            Assert.Contains(badFolderResult.Issues, issue => issue.Code == "FOLDER_ENUMERATION_UNRELIABLE");

            var workspaceResult = await service.BuildPlanAsync(
                jobId,
                new NdTargetSelection
                {
                    Type = NdTargetType.Workspace,
                    Id = "3437-5615-8479",
                    Name = "Workspace"
                },
                new DirectUploadPlanContext
                {
                    JobId = jobId,
                    AllowCreateFolders = false
                },
                CancellationToken.None);

            Assert.True(workspaceResult.CanUpload);
            Assert.Single(workspaceResult.Files);
            Assert.Equal("folder-invoices", workspaceResult.Files[0].DestinationContainerId);
            Assert.DoesNotContain(workspaceResult.Issues, issue => issue.Code == "FOLDER_ENUMERATION_UNRELIABLE");
            Assert.Contains("/v1/Workspace/3437-5615-8479", requestPaths);
            Assert.Contains("/v1/Folder/folder-client-a", requestPaths);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task BuildPlanAsync_WorkspaceFilterFlattensFolderHierarchyAndWarns()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var filePath = Path.Combine(sourceRoot, "client_a", "invoices", "sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "sample content");

        var jobId = Guid.NewGuid().ToString("N");
        var requestPaths = new List<string>();

        try
        {
            await SeedJobWithDoubleNestedFileAsync(dbPath, jobId, sourceRoot, filePath);

            var handler = new StubHttpHandler(request =>
            {
                lock (requestPaths)
                {
                    requestPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var result = await service.BuildPlanAsync(
                jobId,
                new NdTargetSelection
                {
                    Type = NdTargetType.WorkspaceFilter,
                    Id = ":AU2:2:d:e:8:~260209191130554.nev|1",
                    Name = "EMAIL"
                },
                new DirectUploadPlanContext
                {
                    JobId = jobId,
                    AllowCreateFolders = false,
                    CabinetId = "NG-2Q4O0ACP"
                },
                CancellationToken.None);

            Assert.True(result.CanUpload);
            Assert.Single(result.Files);
            Assert.Equal(":AU2:2:d:e:8:~260209191130554.nev|1", result.Files[0].DestinationContainerId);
            Assert.Equal(0, result.PlannedFolderCreates);
            Assert.Contains(result.Issues, issue => issue.Code == "FILTER_FLAT_UPLOAD");
            Assert.DoesNotContain(result.Issues, issue => issue.Severity == DirectUploadIssueSeverity.Error);
            Assert.DoesNotContain(requestPaths, path => path.StartsWith("/v1/Folder/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task BuildPlanAsync_SavedSearchResolvesUploadScopeToWorkspace()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var filePath = Path.Combine(sourceRoot, "sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "sample content");

        var jobId = Guid.NewGuid().ToString("N");
        var requestPaths = new List<string>();
        const string savedSearchId = ":AU2:s:v:5:k:~190409112306006.nev";
        const string workspaceId = ":AU2:o:w:m:v:^W200423132232851.nev";

        try
        {
            await SeedJobWithRootFileAsync(dbPath, jobId, sourceRoot, filePath);

            var handler = new StubHttpHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                var unescapedPath = Uri.UnescapeDataString(path);
                lock (requestPaths)
                {
                    requestPaths.Add(unescapedPath);
                }

                if (string.Equals(unescapedPath, $"/v2/container/{savedSearchId}/info", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, $$"""
                        {
                          "data": {
                            "id": "{{savedSearchId}}",
                            "extension": "ndsq",
                            "workspaceId": "{{workspaceId}}",
                            "description": "DOCX Search"
                          }
                        }
                        """);
                }

                if (string.Equals(unescapedPath, $"/v2/container/{workspaceId}/info", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, $$"""
                        {
                          "data": {
                            "id": "{{workspaceId}}",
                            "extension": "ndws",
                            "description": "Workspace Root"
                          }
                        }
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var result = await service.BuildPlanAsync(
                jobId,
                new NdTargetSelection
                {
                    Type = NdTargetType.WorkspaceFilter,
                    Id = savedSearchId,
                    Name = "DOCX Search",
                    Extension = "ndsq"
                },
                new DirectUploadPlanContext
                {
                    JobId = jobId,
                    CabinetId = "NG-2Q4O0ACP",
                    AllowCreateFolders = false
                },
                CancellationToken.None);

            Assert.True(result.CanUpload);
            Assert.Single(result.Files);
            Assert.Equal(workspaceId, result.Files[0].DestinationContainerId);
            Assert.Contains(result.Issues, issue => issue.Code == "SAVED_SEARCH_SCOPE_INFERRED");
            Assert.DoesNotContain(result.Issues, issue => issue.Code == "SAVED_SEARCH_SCOPE_UNRESOLVED");
            Assert.DoesNotContain(result.Issues, issue => issue.Code == "FILTER_FLAT_UPLOAD");
            Assert.Contains(requestPaths, path => string.Equals(path, $"/v2/container/{savedSearchId}/info", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(requestPaths, path => string.Equals(path, $"/v2/container/{workspaceId}/info", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task BuildPlanAsync_CollabspaceUsesV2ContainerSubListingInsteadOfFolderEndpoint()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var filePath = Path.Combine(sourceRoot, "client_a", "sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "sample content");

        var jobId = Guid.NewGuid().ToString("N");
        var requestPaths = new List<string>();

        try
        {
            await SeedJobWithSingleNestedFileAsync(dbPath, jobId, sourceRoot, filePath);

            var handler = new StubHttpHandler(request =>
            {
                var absolutePath = request.RequestUri?.AbsolutePath ?? string.Empty;
                var query = request.RequestUri?.Query ?? string.Empty;
                var unescapedPath = Uri.UnescapeDataString(absolutePath);
                lock (requestPaths)
                {
                    requestPaths.Add($"{absolutePath}{query}");
                }

                if (unescapedPath.StartsWith("/v2/container/:AU2:z:g:r:t:^C230123140133608.nev|1/sub", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "Results": [
                            {
                              "EnvId": "folder-client-a",
                              "Attributes": {
                                "Description": "client_a",
                                "Ext": "ndfld"
                              }
                            }
                          ]
                        }
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var result = await service.BuildPlanAsync(
                jobId,
                new NdTargetSelection
                {
                    Type = NdTargetType.Folder,
                    Id = ":AU2:z:g:r:t:^C230123140133608.nev|1",
                    Name = "quick share"
                },
                new DirectUploadPlanContext
                {
                    JobId = jobId,
                    AllowCreateFolders = false,
                    CabinetId = "NG-2Q4O0ACP"
                },
                CancellationToken.None);

            Assert.True(result.CanUpload);
            Assert.Single(result.Files);
            Assert.Equal("folder-client-a", result.Files[0].DestinationContainerId);
            Assert.DoesNotContain(result.Issues, issue => issue.Severity == DirectUploadIssueSeverity.Error);
            Assert.Contains(requestPaths, path => Uri.UnescapeDataString(path).StartsWith("/v2/container/:AU2:z:g:r:t:^C230123140133608.nev|1/sub", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(requestPaths, path => path.StartsWith("/v1/Folder/%3AAU2%3Az%3Ag%3Ar%3At%3A%5EC230123140133608.nev%7C1", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task BuildPlanAsync_WhenFolderCreateForbidden_ReportsExplicitPermissionIssue()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var filePath = Path.Combine(sourceRoot, "client_a", "invoices", "sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "sample content");

        var jobId = Guid.NewGuid().ToString("N");

        try
        {
            await SeedJobWithDoubleNestedFileAsync(dbPath, jobId, sourceRoot, filePath);

            var handler = new StubHttpHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path == "/v1/Workspace/3470-9010-7660")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"list":[{"id":"folder-client-a","name":"client_a","type":"ndfld"}]}""");
                }

                if (path == "/v1/Folder/folder-client-a")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"list":[],"sortOrder":"name"}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v1/Folder")
                {
                    return JsonResponse(HttpStatusCode.Forbidden, """{"error":"No rights on parent folder to create subfolders"}""");
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var result = await service.BuildPlanAsync(
                jobId,
                new NdTargetSelection
                {
                    Type = NdTargetType.Workspace,
                    Id = "3470-9010-7660",
                    Name = "Workspace"
                },
                new DirectUploadPlanContext
                {
                    JobId = jobId,
                    AllowCreateFolders = true,
                    CabinetId = "NG-2Q4O0ACP"
                },
                CancellationToken.None);

            Assert.False(result.CanUpload);
            Assert.Empty(result.Files);
            Assert.Contains(result.Issues, issue =>
                issue.Code == "FOLDER_CREATE_FORBIDDEN" &&
                string.Equals(issue.RelativePath, "client_a/invoices", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Issues, issue =>
                issue.Code == "FOLDER_RESOLVE_FAILED" &&
                string.Equals(issue.RelativePath, "client_a/invoices", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_UsesV1DocumentPathWithoutIndexPriorityByDefault()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var filePath = Path.Combine(tempRoot, "sample.txt");
        await File.WriteAllTextAsync(filePath, "sample content");
        var requests = new List<Uri>();

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                lock (requests)
                {
                    requests.Add(request.RequestUri!);
                }

                return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var plan = CreatePlan(filePath);
            var context = new DirectUploadPlanContext
            {
                MaxConcurrency = 1,
                MaxRetryAttempts = 1
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.Single(result.Files);
            Assert.True(result.Files[0].Succeeded);
            Assert.Single(requests);
            Assert.Equal("/v1/Document", requests[0].AbsolutePath);
            Assert.True(string.IsNullOrWhiteSpace(requests[0].Query));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_AppendsIndexPriorityQueryWhenConfigured()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var filePath = Path.Combine(tempRoot, "sample.txt");
        await File.WriteAllTextAsync(filePath, "sample content");
        var requests = new List<Uri>();

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                lock (requests)
                {
                    requests.Add(request.RequestUri!);
                }

                return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var plan = CreatePlan(filePath);
            var context = new DirectUploadPlanContext
            {
                MaxConcurrency = 1,
                MaxRetryAttempts = 1,
                V1DocumentIndexPriority = 7
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.Single(result.Files);
            Assert.True(result.Files[0].Succeeded);
            Assert.Single(requests);
            Assert.Equal("/v1/Document", requests[0].AbsolutePath);
            Assert.Equal("indexpriority=7", requests[0].Query.TrimStart('?'));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static UploadPlanResult CreatePlan(string filePath)
    {
        return new UploadPlanResult
        {
            TotalRequestedFiles = 1,
            PlannedFiles = 1,
            SkippedFiles = 0,
            CanUpload = true,
            Files = new[]
            {
                new UploadPlanFileEntry(
                    FileId: Guid.NewGuid().ToString("N"),
                    RelativePath: "sample.txt",
                    FullPath: filePath,
                    SizeBytes: new FileInfo(filePath).Length,
                    DestinationContainerId: "D-DESTINATION",
                    ProfileValues: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Acl: null,
                    UseMultipartUpload: false)
            }
        };
    }

    private static async Task SeedJobWithSingleNestedFileAsync(string dbPath, string jobId, string sourceRoot, string filePath)
    {
        var store = new JobStore(dbPath);
        await store.InitializeAsync();
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, sourceRoot, "Complete"));

        var rootFolderId = Guid.NewGuid().ToString("N");
        await store.InsertFolderAsync(new FolderRecord(
            rootFolderId,
            jobId,
            sourceRoot,
            string.Empty,
            null,
            0,
            true,
            false,
            DateTime.UtcNow,
            "include",
            "inherit"));

        var childFolderId = Guid.NewGuid().ToString("N");
        await store.InsertFolderAsync(new FolderRecord(
            childFolderId,
            jobId,
            Path.Combine(sourceRoot, "client_a"),
            "client_a",
            rootFolderId,
            1,
            true,
            false,
            DateTime.UtcNow,
            "inherit",
            "inherit"));

        await store.InsertFileAsync(new FileRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            filePath,
            "client_a\\sample.txt",
            new FileInfo(filePath).Length,
            DateTime.UtcNow,
            false,
            childFolderId,
            "inherit",
            null));
    }

    private static async Task SeedJobWithRootFileAsync(string dbPath, string jobId, string sourceRoot, string filePath)
    {
        var store = new JobStore(dbPath);
        await store.InitializeAsync();
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, sourceRoot, "Complete"));

        var rootFolderId = Guid.NewGuid().ToString("N");
        await store.InsertFolderAsync(new FolderRecord(
            rootFolderId,
            jobId,
            sourceRoot,
            string.Empty,
            null,
            0,
            true,
            false,
            DateTime.UtcNow,
            "include",
            "inherit"));

        await store.InsertFileAsync(new FileRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            filePath,
            "sample.txt",
            new FileInfo(filePath).Length,
            DateTime.UtcNow,
            false,
            rootFolderId,
            "inherit",
            null));
    }

    private static async Task SeedJobWithDoubleNestedFileAsync(string dbPath, string jobId, string sourceRoot, string filePath)
    {
        var store = new JobStore(dbPath);
        await store.InitializeAsync();
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, sourceRoot, "Complete"));

        var rootFolderId = Guid.NewGuid().ToString("N");
        await store.InsertFolderAsync(new FolderRecord(
            rootFolderId,
            jobId,
            sourceRoot,
            string.Empty,
            null,
            0,
            true,
            false,
            DateTime.UtcNow,
            "include",
            "inherit"));

        var clientFolderId = Guid.NewGuid().ToString("N");
        await store.InsertFolderAsync(new FolderRecord(
            clientFolderId,
            jobId,
            Path.Combine(sourceRoot, "client_a"),
            "client_a",
            rootFolderId,
            1,
            true,
            false,
            DateTime.UtcNow,
            "inherit",
            "inherit"));

        var invoicesFolderId = Guid.NewGuid().ToString("N");
        await store.InsertFolderAsync(new FolderRecord(
            invoicesFolderId,
            jobId,
            Path.Combine(sourceRoot, "client_a", "invoices"),
            "client_a\\invoices",
            clientFolderId,
            2,
            true,
            false,
            DateTime.UtcNow,
            "inherit",
            "inherit"));

        await store.InsertFileAsync(new FileRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            filePath,
            "client_a\\invoices\\sample.txt",
            new FileInfo(filePath).Length,
            DateTime.UtcNow,
            false,
            invoicesFolderId,
            "inherit",
            null));
    }

    private static NetDocumentsDirectUploadService CreateDirectUploadService(HttpMessageHandler handler, string dbPath)
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
        return new NetDocumentsDirectUploadService(apiClient, store);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string payload)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private static string CreateTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-direct-upload-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static void CleanupTempRoot(string tempRoot)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
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
