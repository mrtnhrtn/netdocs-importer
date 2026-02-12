using System.Net;
using System.Text;
using NetDocsImporter.Core;
using NetDocsImporter.Data;
using NetDocsImporter.NetDocs;

namespace NetDocsImporter.Tests;

public class NetDocumentsDirectUploadServiceTests
{
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
