using System.Net;
using System.Text;
using System.IO.Compression;
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
    public async Task BuildPlanAsync_WorkspaceRootCreatesUseResolvedWorkspaceParentId()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var filePath = Path.Combine(sourceRoot, "client_a", "invoices", "sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "sample content");

        var jobId = Guid.NewGuid().ToString("N");
        const string workspaceEnvId = ":AU2:o:w:m:v:^W200423132232851.nev";
        const string workspaceLegacyId = "3470-9010-7660";
        const string clientFolderId = "folder-client-a-01-02";
        const string invoicesFolderId = "folder-invoices-01-02";
        var createParentValues = new List<string>();

        try
        {
            await SeedJobWithDoubleNestedFileAsync(dbPath, jobId, sourceRoot, filePath);

            var handler = new StubHttpHandler(request =>
            {
                var absolutePath = request.RequestUri?.AbsolutePath ?? string.Empty;
                var unescapedPath = Uri.UnescapeDataString(absolutePath);

                if (string.Equals(unescapedPath, $"/v2/container/{workspaceEnvId}/info", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, $$"""
                        {
                          "data": {
                            "id": "{{workspaceLegacyId}}",
                            "extension": "ndws",
                            "name": "Workspace Root"
                          }
                        }
                        """);
                }

                if (absolutePath == $"/v1/Workspace/{workspaceLegacyId}")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"list":[],"sortOrder":"name"}""");
                }

                if (absolutePath == $"/v1/Folder/{clientFolderId}")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"list":[],"sortOrder":"name"}""");
                }

                if (request.Method == HttpMethod.Post && absolutePath == "/v1/Folder")
                {
                    var form = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                    var parent = ReadFormUrlEncodedValue(form, "parent") ?? string.Empty;
                    var name = ReadFormUrlEncodedValue(form, "name") ?? string.Empty;
                    lock (createParentValues)
                    {
                        createParentValues.Add(parent);
                    }

                    if (string.Equals(parent, workspaceLegacyId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(name, "client_a", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponse(HttpStatusCode.OK, $$"""{"id":"{{clientFolderId}}","type":"ndfld"}""");
                    }

                    if (string.Equals(parent, clientFolderId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(name, "invoices", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponse(HttpStatusCode.OK, $$"""{"id":"{{invoicesFolderId}}","type":"ndfld"}""");
                    }

                    return JsonResponse(HttpStatusCode.BadRequest, """{"error":"unexpected folder create payload"}""");
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var result = await service.BuildPlanAsync(
                jobId,
                new NdTargetSelection
                {
                    Type = NdTargetType.Workspace,
                    Id = workspaceEnvId,
                    Name = "Workspace"
                },
                new DirectUploadPlanContext
                {
                    JobId = jobId,
                    AllowCreateFolders = true,
                    CabinetId = "NG-2Q4O0ACP"
                },
                CancellationToken.None);

            Assert.True(
                result.CanUpload,
                $"canUpload={result.CanUpload}, plannedFiles={result.PlannedFiles}, issues={string.Join(" | ", result.Issues.Select(i => $"{i.Code}:{i.Message}:{i.RelativePath}"))}");
            Assert.Single(result.Files);
            Assert.Equal(invoicesFolderId, result.Files[0].DestinationContainerId);
            Assert.Equal(2, result.PlannedFolderCreates);
            Assert.Equal(2, createParentValues.Count);
            Assert.Equal(workspaceLegacyId, createParentValues[0]);
            Assert.Equal(clientFolderId, createParentValues[1]);
            Assert.DoesNotContain(createParentValues, parent => string.Equals(parent, workspaceEnvId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task BuildPlanAsync_IncludesWorkspaceDefaultsInPlannedFileProfiles()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var filePath = Path.Combine(sourceRoot, "sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "sample content");

        var jobId = Guid.NewGuid().ToString("N");

        try
        {
            await SeedJobWithRootFileAsync(dbPath, jobId, sourceRoot, filePath);
            var handler = new StubHttpHandler(_ => JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}"""));

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
                    AllowCreateFolders = false,
                    EffectiveProfileDefaults = new EffectiveProfileDefaults
                    {
                        ValuesByAttributeId = new Dictionary<string, NdProfileValue>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["client"] = new NdProfileValue
                            {
                                AttributeId = "client",
                                AttributeName = "Client",
                                RawValue = "ACME"
                            }
                        }
                    }
                },
                CancellationToken.None);

            Assert.True(result.CanUpload);
            Assert.Single(result.Files);
            Assert.Equal("3470-9010-7660", result.Files[0].DestinationContainerId);
            Assert.Equal("ACME", result.Files[0].ProfileValues["Client"]);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task BuildPlanAsync_BlocksSecurityRestrictedFileExtensions()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var filePath = Path.Combine(sourceRoot, "payload.EXE");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "not-a-real-executable");

        var jobId = Guid.NewGuid().ToString("N");

        try
        {
            await SeedJobWithRootFileAsync(dbPath, jobId, sourceRoot, filePath);
            var handler = new StubHttpHandler(_ => JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}"""));

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
                    AllowCreateFolders = false
                },
                CancellationToken.None);

            Assert.False(result.CanUpload);
            Assert.Equal(0, result.PlannedFiles);
            Assert.Equal(1, result.SkippedFiles);
            Assert.Contains(
                result.Issues,
                issue => issue.Code == "BLOCKED_FILE_EXTENSION" &&
                         issue.Severity == DirectUploadIssueSeverity.Error &&
                         issue.Message.Contains(".EXE", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task BuildPlanAsync_IncludesAttributeNumberKeysWhenAttributeMetadataExists()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var filePath = Path.Combine(sourceRoot, "sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "sample content");

        var jobId = Guid.NewGuid().ToString("N");

        try
        {
            await SeedJobWithRootFileAsync(dbPath, jobId, sourceRoot, filePath);
            var store = new JobStore(dbPath);
            await store.InitializeAsync();
            await store.ReplaceNetDocumentsAttributesAsync(
                "NG-CAB",
                new[]
                {
                    new NetDocumentsAttributeRecord("NG-CAB", "NG-REPO", 2, "2", "Client", "lookup", true, false, true, null, false, DateTime.UtcNow),
                    new NetDocumentsAttributeRecord("NG-CAB", "NG-REPO", 3, "3", "Matter", "lookup", true, false, true, 2, true, DateTime.UtcNow)
                });

            var handler = new StubHttpHandler(_ => JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}"""));

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
                    CabinetId = "NG-CAB",
                    AllowCreateFolders = false,
                    EffectiveProfileDefaults = new EffectiveProfileDefaults
                    {
                        ValuesByAttributeId = new Dictionary<string, NdProfileValue>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["2"] = new NdProfileValue
                            {
                                AttributeId = "2",
                                AttributeName = "Client",
                                RawValue = "0004"
                            },
                            ["3"] = new NdProfileValue
                            {
                                AttributeId = "3",
                                AttributeName = "Matter",
                                RawValue = "002"
                            }
                        }
                    }
                },
                CancellationToken.None);

            Assert.True(result.CanUpload);
            Assert.Single(result.Files);
            Assert.Equal("0004", result.Files[0].ProfileValues["Client"]);
            Assert.Equal("002", result.Files[0].ProfileValues["Matter"]);
            Assert.Equal("0004", result.Files[0].ProfileValues["2"]);
            Assert.Equal("002", result.Files[0].ProfileValues["3"]);
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
    public async Task UploadAsync_UsesDefaultIndexPriorityWhenNotConfigured()
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
            Assert.Equal("indexpriority=250", requests[0].Query.TrimStart('?'));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_V1Upload_ExtractsDocumentIdFromStandardAttributes()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var filePath = Path.Combine(tempRoot, "sample.txt");
        await File.WriteAllTextAsync(filePath, "sample content");
        string? requestBody = null;

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.Method == HttpMethod.Post &&
                    string.Equals(request.RequestUri?.AbsolutePath, "/v1/Document", StringComparison.OrdinalIgnoreCase))
                {
                    requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                    return JsonResponse(HttpStatusCode.OK, """{"standardAttributes":{"id":"D-V1-123"}}""");
                }

                return JsonResponse(HttpStatusCode.BadRequest, """{"error":"unexpected request"}""");
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
            Assert.Equal("D-V1-123", result.Files[0].DocumentId);
            Assert.False(string.IsNullOrWhiteSpace(requestBody));
            Assert.True(
                requestBody!.Contains("name=\"return\"", StringComparison.OrdinalIgnoreCase) ||
                requestBody.Contains("name=return", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("standardAttributes", requestBody!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_RespectsAddToRecentsFlagForV1AndMultipartCreate()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var filePath = Path.Combine(tempRoot, "sample.bin");
        await File.WriteAllBytesAsync(filePath, Enumerable.Repeat((byte)5, 8).ToArray());
        var v1DocumentBodies = new List<string>();
        var v1DocumentCallCount = 0;

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (request.Method == HttpMethod.Post && path == "/v1/Document")
                {
                    var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        lock (v1DocumentBodies)
                        {
                            v1DocumentBodies.Add(body);
                        }
                    }

                    v1DocumentCallCount++;
                    return v1DocumentCallCount == 1
                        ? JsonResponse(HttpStatusCode.OK, """{"standardAttributes":{"id":"D-V1-ADDRECENTS"}}""")
                        : JsonResponse(HttpStatusCode.OK, """{"standardAttributes":{"id":"D-MP-ADDRECENTS"}}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/D-MP-ADDRECENTS/1/initiate")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"uploadId":"up-addrecents"}""");
                }

                if (request.Method == HttpMethod.Put && path == "/v2/document/upload/up-addrecents/part/1")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/complete/up-addrecents")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
                }

                return JsonResponse(HttpStatusCode.BadRequest, """{"error":"unexpected request"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);

            var v1Result = await service.UploadAsync(
                CreatePlan(filePath, useMultipartUpload: false),
                new DirectUploadPlanContext
                {
                    MaxConcurrency = 1,
                    MaxRetryAttempts = 1,
                    AddToRecents = true
                },
                cancellationToken: CancellationToken.None);

            Assert.Single(v1Result.Files);
            Assert.True(v1Result.Files[0].Succeeded);
            Assert.Equal("D-V1-ADDRECENTS", v1Result.Files[0].DocumentId);
            Assert.True(v1DocumentBodies.Count >= 1);
            var v1Body = v1DocumentBodies[0];
            Assert.True(
                v1Body.Contains("name=\"addToRecents\"", StringComparison.OrdinalIgnoreCase) ||
                v1Body.Contains("name=addToRecents", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("true", v1Body, StringComparison.OrdinalIgnoreCase);

            var multipartResult = await service.UploadAsync(
                CreatePlan(filePath, useMultipartUpload: true),
                new DirectUploadPlanContext
                {
                    MaxConcurrency = 1,
                    MaxRetryAttempts = 1,
                    MultipartChunkSizeBytes = 1024 * 1024,
                    AddToRecents = false
                },
                cancellationToken: CancellationToken.None);

            Assert.Single(multipartResult.Files);
            Assert.True(multipartResult.Files[0].Succeeded);
            Assert.Equal("D-MP-ADDRECENTS", multipartResult.Files[0].DocumentId);
            Assert.True(v1DocumentBodies.Count >= 2);
            var multipartCreateBody = v1DocumentBodies[1];
            Assert.True(
                multipartCreateBody.Contains("name=\"addToRecents\"", StringComparison.OrdinalIgnoreCase) ||
                multipartCreateBody.Contains("name=addToRecents", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("false", multipartCreateBody, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task UploadAsync_V1Document_UsesDerivedUploadHost()
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

            var service = CreateDirectUploadService(handler, dbPath, "https://api.au.netdocuments.com");
            var plan = CreatePlan(filePath);
            var context = new DirectUploadPlanContext
            {
                ApiBaseUrl = "https://api.au.netdocuments.com",
                MaxConcurrency = 1,
                MaxRetryAttempts = 1
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.Single(result.Files);
            Assert.True(result.Files[0].Succeeded);
            Assert.Single(requests);
            Assert.Equal("upload.au.netdocuments.com", requests[0].Host);
            Assert.Equal("/v1/Document", requests[0].AbsolutePath);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_MultipartEndpoints_UseDerivedUploadHost()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var filePath = Path.Combine(tempRoot, "sample.bin");
        await File.WriteAllBytesAsync(filePath, Enumerable.Repeat((byte)1, 8).ToArray());
        var requests = new List<Uri>();

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                lock (requests)
                {
                    requests.Add(request.RequestUri!);
                }

                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (request.Method == HttpMethod.Post && path == "/v1/Document")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardAttributes":{"id":"D-CREATED"}}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/D-CREATED/1/initiate")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"uploadId":"up-1"}""");
                }

                if (request.Method == HttpMethod.Put && path == "/v2/document/upload/up-1/part/1")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/complete/up-1")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
                }

                return JsonResponse(HttpStatusCode.BadRequest, """{"error":"unexpected request"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath, "https://api.au.netdocuments.com");
            var plan = CreatePlan(filePath, useMultipartUpload: true);
            var context = new DirectUploadPlanContext
            {
                ApiBaseUrl = "https://api.au.netdocuments.com",
                MaxConcurrency = 1,
                MaxRetryAttempts = 1,
                MultipartChunkSizeBytes = 1024 * 1024
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.Single(result.Files);
            Assert.True(result.Files[0].Succeeded);
            Assert.Contains(requests, r => r.AbsolutePath == "/v1/Document" && r.Host == "upload.au.netdocuments.com");
            Assert.Contains(requests, r => r.AbsolutePath == "/v2/document/D-CREATED/1/initiate" && r.Host == "upload.au.netdocuments.com");
            Assert.Contains(requests, r => r.AbsolutePath == "/v2/document/upload/up-1/part/1" && r.Host == "upload.au.netdocuments.com");
            Assert.Contains(requests, r => r.AbsolutePath == "/v2/document/complete/up-1" && r.Host == "upload.au.netdocuments.com");
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_NonUploadApiCallsRemainOnApiHost()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var filePath = Path.Combine(sourceRoot, "client_a", "sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "sample content");
        var jobId = Guid.NewGuid().ToString("N");
        var requestUris = new List<Uri>();

        try
        {
            await SeedJobWithSingleNestedFileAsync(dbPath, jobId, sourceRoot, filePath);

            var handler = new StubHttpHandler(request =>
            {
                if (request.RequestUri is not null)
                {
                    lock (requestUris)
                    {
                        requestUris.Add(request.RequestUri);
                    }
                }

                if (request.RequestUri?.AbsolutePath == "/v1/Folder/3470-9157-8890")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"list":[{"envId":":AU2:i:e:9:8:~211201092644749.nev","type":"doc"}],"sortOrder":"name"}""");
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath, "https://api.au.netdocuments.com");
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
                    ApiBaseUrl = "https://api.au.netdocuments.com",
                    JobId = jobId,
                    AllowCreateFolders = false
                },
                CancellationToken.None);

            Assert.True(result.CanUpload);
            Assert.NotEmpty(requestUris);
            Assert.All(requestUris, uri => Assert.Equal("api.au.netdocuments.com", uri.Host));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_DerivationFallback_UsesApiHostWhenApiHostNotApiPrefixed()
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

            var service = CreateDirectUploadService(handler, dbPath, "https://custom.example.com");
            var plan = CreatePlan(filePath);
            var context = new DirectUploadPlanContext
            {
                ApiBaseUrl = "https://custom.example.com",
                MaxConcurrency = 1,
                MaxRetryAttempts = 1
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.Single(result.Files);
            Assert.True(result.Files[0].Succeeded);
            Assert.Single(requests);
            Assert.Equal("custom.example.com", requests[0].Host);
            Assert.Equal("/v1/Document", requests[0].AbsolutePath);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_Derivation_WorksForNetvoyageVaultHost()
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

            var service = CreateDirectUploadService(handler, dbPath, "https://api.vault.netvoyage.com");
            var plan = CreatePlan(filePath);
            var context = new DirectUploadPlanContext
            {
                ApiBaseUrl = "https://api.vault.netvoyage.com",
                MaxConcurrency = 1,
                MaxRetryAttempts = 1
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.Single(result.Files);
            Assert.True(result.Files[0].Succeeded);
            Assert.Single(requests);
            Assert.Equal("upload.vault.netvoyage.com", requests[0].Host);
            Assert.Equal("/v1/Document", requests[0].AbsolutePath);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_IncludesCustomAttributesPayloadForNumericAttributeKeys()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var filePath = Path.Combine(tempRoot, "sample.txt");
        await File.WriteAllTextAsync(filePath, "sample content");
        string? requestBody = null;

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.Method == HttpMethod.Post &&
                    string.Equals(request.RequestUri?.AbsolutePath, "/v1/Document", StringComparison.OrdinalIgnoreCase))
                {
                    requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                }

                return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var plan = new UploadPlanResult
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
                        ProfileValues: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["2"] = "0004",
                            ["3"] = "002",
                            ["Client"] = "0004",
                            ["Matter"] = "002"
                        },
                        Acl: null,
                        UseMultipartUpload: false)
                }
            };

            var context = new DirectUploadPlanContext
            {
                MaxConcurrency = 1,
                MaxRetryAttempts = 1
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.Single(result.Files);
            Assert.True(result.Files[0].Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(requestBody));
            Assert.True(
                requestBody!.Contains("name=\"profile\"", StringComparison.OrdinalIgnoreCase) ||
                requestBody.Contains("name=profile", StringComparison.OrdinalIgnoreCase));
            Assert.True(
                requestBody.Contains("name=\"partialProfiling\"", StringComparison.OrdinalIgnoreCase) ||
                requestBody.Contains("name=partialProfiling", StringComparison.OrdinalIgnoreCase));
            Assert.True(
                requestBody.Contains("name=\"failOnError\"", StringComparison.OrdinalIgnoreCase) ||
                requestBody.Contains("name=failOnError", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("true", requestBody!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"customAttributes\"", requestBody!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"id\":2", requestBody!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"id\":3", requestBody!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"value\":\"0004\"", requestBody!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"value\":\"002\"", requestBody!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task BuildPlanAsync_AddsMultipartIssueCodesForLargeFiles()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var filePath = Path.Combine(sourceRoot, "large.bin");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllBytesAsync(filePath, new byte[15]);
        var jobId = Guid.NewGuid().ToString("N");

        try
        {
            await SeedJobWithRootFileAsync(dbPath, jobId, sourceRoot, filePath);
            var service = CreateDirectUploadService(
                new StubHttpHandler(_ => JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""")),
                dbPath);

            var multipartEnabled = await service.BuildPlanAsync(
                jobId,
                new NdTargetSelection { Type = NdTargetType.Folder, Id = "D-DESTINATION", Name = "Destination" },
                new DirectUploadPlanContext
                {
                    JobId = jobId,
                    AllowCreateFolders = false,
                    EnableMultipartUpload = true,
                    MultipartThresholdBytes = 10,
                    MultipartMaxFileSizeBytes = 20
                },
                CancellationToken.None);
            Assert.Contains(multipartEnabled.Issues, issue => issue.Code == "MULTIPART_REQUIRED");
            Assert.True(multipartEnabled.Files[0].UseMultipartUpload);

            var multipartDisabled = await service.BuildPlanAsync(
                jobId,
                new NdTargetSelection { Type = NdTargetType.Folder, Id = "D-DESTINATION", Name = "Destination" },
                new DirectUploadPlanContext
                {
                    JobId = jobId,
                    AllowCreateFolders = false,
                    EnableMultipartUpload = false,
                    MultipartThresholdBytes = 10,
                    MultipartMaxFileSizeBytes = 20
                },
                CancellationToken.None);
            Assert.Contains(multipartDisabled.Issues, issue => issue.Code == "MULTIPART_DISABLED_FOR_LARGE_FILE");
            Assert.Contains(multipartDisabled.Issues, issue => issue.Severity == DirectUploadIssueSeverity.Error);

            var multipartTooLarge = await service.BuildPlanAsync(
                jobId,
                new NdTargetSelection { Type = NdTargetType.Folder, Id = "D-DESTINATION", Name = "Destination" },
                new DirectUploadPlanContext
                {
                    JobId = jobId,
                    AllowCreateFolders = false,
                    EnableMultipartUpload = true,
                    MultipartThresholdBytes = 10,
                    MultipartMaxFileSizeBytes = 14
                },
                CancellationToken.None);
            Assert.Contains(multipartTooLarge.Issues, issue => issue.Code == "MULTIPART_MAX_SIZE_EXCEEDED");
            Assert.Contains(multipartTooLarge.Issues, issue => issue.Severity == DirectUploadIssueSeverity.Error);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_UsesV2MultipartEndpointsWhenUseMultipartUploadTrue()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var filePath = Path.Combine(tempRoot, "sample.bin");
        await File.WriteAllBytesAsync(filePath, Enumerable.Repeat((byte)1, 8).ToArray());
        var requests = new List<(string Method, string Path)>();

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                lock (requests)
                {
                    requests.Add((request.Method.Method, path));
                }

                if (request.Method == HttpMethod.Post && path == "/v1/Document")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardAttributes":{"id":"D-CREATED"}}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/D-CREATED/1/initiate")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"uploadId":"up-1"}""");
                }

                if (request.Method == HttpMethod.Put && path == "/v2/document/upload/up-1/part/1")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/complete/up-1")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
                }

                return JsonResponse(HttpStatusCode.BadRequest, """{"error":"unexpected request"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var plan = CreatePlan(filePath, useMultipartUpload: true);
            var context = new DirectUploadPlanContext
            {
                MaxConcurrency = 1,
                MaxRetryAttempts = 1,
                MultipartChunkSizeBytes = 1024 * 1024
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.Single(result.Files);
            Assert.True(result.Files[0].Succeeded);
            Assert.Equal("D-CREATED", result.Files[0].DocumentId);
            Assert.Contains(requests, r => r == ("POST", "/v1/Document"));
            Assert.Contains(requests, r => r == ("POST", "/v2/document/D-CREATED/1/initiate"));
            Assert.Contains(requests, r => r == ("PUT", "/v2/document/upload/up-1/part/1"));
            Assert.Contains(requests, r => r == ("POST", "/v2/document/complete/up-1"));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_MultipartInitiate_UsesCreatedDocumentIdInsteadOfDestinationId()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var filePath = Path.Combine(tempRoot, "sample.bin");
        await File.WriteAllBytesAsync(filePath, Enumerable.Repeat((byte)4, 8).ToArray());
        var requests = new List<(string Method, string Path)>();
        const string destinationId = ":AU1:8:3:1:o:^W160802111732575.nev";

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                lock (requests)
                {
                    requests.Add((request.Method.Method, path));
                }

                if (request.Method == HttpMethod.Post && path == "/v1/Document")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardAttributes":{"id":"D-NEWDOC"}}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/D-NEWDOC/1/initiate")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"uploadId":"up-raw"}""");
                }

                if (request.Method == HttpMethod.Put && path == "/v2/document/upload/up-raw/part/1")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/complete/up-raw")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
                }

                return JsonResponse(HttpStatusCode.BadRequest, """{"error":"unexpected request"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var plan = CreatePlan(filePath, useMultipartUpload: true, destinationContainerId: destinationId);
            var context = new DirectUploadPlanContext
            {
                MaxConcurrency = 1,
                MaxRetryAttempts = 1,
                MultipartChunkSizeBytes = 1024 * 1024
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.Single(result.Files);
            Assert.True(result.Files[0].Succeeded);
            Assert.Contains(requests, r => r == ("POST", "/v1/Document"));
            Assert.Contains(requests, r => r == ("POST", "/v2/document/D-NEWDOC/1/initiate"));
            Assert.DoesNotContain(
                requests,
                r => r.Method == "POST" &&
                     Uri.UnescapeDataString(r.Path).Contains(destinationId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_MultipartPartRetriesOnTransientFailure()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var filePath = Path.Combine(tempRoot, "sample.bin");
        await File.WriteAllBytesAsync(filePath, Enumerable.Repeat((byte)3, 16).ToArray());
        var partAttempts = 0;
        var completeCalls = 0;

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (request.Method == HttpMethod.Post && path == "/v1/Document")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardAttributes":{"id":"D-RETRY"}}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/D-RETRY/1/initiate")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"uploadId":"up-retry"}""");
                }

                if (request.Method == HttpMethod.Put && path == "/v2/document/upload/up-retry/part/1")
                {
                    partAttempts++;
                    if (partAttempts == 1)
                    {
                        return JsonResponse(HttpStatusCode.ServiceUnavailable, """{"error":"try again"}""");
                    }

                    return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/complete/up-retry")
                {
                    completeCalls++;
                    return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
                }

                return JsonResponse(HttpStatusCode.BadRequest, """{"error":"unexpected request"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var plan = CreatePlan(filePath, useMultipartUpload: true);
            var context = new DirectUploadPlanContext
            {
                MaxConcurrency = 1,
                MaxRetryAttempts = 1,
                MultipartPartMaxRetryAttempts = 2,
                MultipartChunkSizeBytes = 1024 * 1024
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.True(result.Files[0].Succeeded);
            Assert.Equal(2, partAttempts);
            Assert.Equal(1, completeCalls);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_DoesNotCallCompleteWhenMultipartPartFails()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var filePath = Path.Combine(tempRoot, "sample.bin");
        await File.WriteAllBytesAsync(filePath, Enumerable.Repeat((byte)7, 16).ToArray());
        var completeCalls = 0;

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (request.Method == HttpMethod.Post && path == "/v1/Document")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardAttributes":{"id":"D-FAIL"}}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/D-FAIL/1/initiate")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"uploadId":"up-fail"}""");
                }

                if (request.Method == HttpMethod.Put && path == "/v2/document/upload/up-fail/part/1")
                {
                    return JsonResponse(HttpStatusCode.InternalServerError, """{"error":"part failed"}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/complete/up-fail")
                {
                    completeCalls++;
                    return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
                }

                return JsonResponse(HttpStatusCode.BadRequest, """{"error":"unexpected request"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var plan = CreatePlan(filePath, useMultipartUpload: true);
            var context = new DirectUploadPlanContext
            {
                MaxConcurrency = 1,
                MaxRetryAttempts = 1,
                MultipartPartMaxRetryAttempts = 1,
                MultipartChunkSizeBytes = 1024 * 1024
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.False(result.Files[0].Succeeded);
            Assert.Equal(0, completeCalls);
            Assert.Contains("Multipart part 1 failed", result.Files[0].Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_DocumentCreateAkamaiBlock_ReportsSingleLineDecodedSnippet()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var filePath = Path.Combine(tempRoot, "sample.bin");
        await File.WriteAllBytesAsync(filePath, Enumerable.Repeat((byte)6, 8).ToArray());

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (request.Method == HttpMethod.Post && path == "/v1/Document")
                {
                    const string akamaiHtml = """
                        <HTML><HEAD>
                        <TITLE>Access Denied</TITLE>
                        </HEAD><BODY>
                        <H1>Access Denied</H1>
                        You don't have permission to access ""http&#58;&#47;&#47;upload&#46;au&#46;netdocuments&#46;com&#47;v1&#47;Document&#63;"" on this server.<P>
                        Reference&#32;&#35;18&
                        </BODY></HTML>
                        """;
                    return HtmlResponse(HttpStatusCode.Forbidden, akamaiHtml);
                }

                return JsonResponse(HttpStatusCode.BadRequest, """{"error":"unexpected request"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var plan = CreatePlan(filePath, useMultipartUpload: false);
            var context = new DirectUploadPlanContext
            {
                ApiBaseUrl = "https://api.au.netdocuments.com",
                MaxConcurrency = 1,
                MaxRetryAttempts = 1
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.False(result.Files[0].Succeeded);
            Assert.Equal(403, result.Files[0].HttpStatus);
            Assert.Contains("Akamai WAF Access Denied", result.Files[0].Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("http://upload.au.netdocuments.com/v1/Document?", result.Files[0].Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("reference='18'", result.Files[0].Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('\r', result.Files[0].Message);
            Assert.DoesNotContain('\n', result.Files[0].Message);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UploadAsync_MultipartCompleteFailure_ReportsDecodedCompressedSnippet()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var filePath = Path.Combine(tempRoot, "sample.bin");
        await File.WriteAllBytesAsync(filePath, Enumerable.Repeat((byte)9, 8).ToArray());

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (request.Method == HttpMethod.Post && path == "/v1/Document")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardAttributes":{"id":"D-COMPRESS"}}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/D-COMPRESS/1/initiate")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"uploadId":"up-compress"}""");
                }

                if (request.Method == HttpMethod.Put && path == "/v2/document/upload/up-compress/part/1")
                {
                    return JsonResponse(HttpStatusCode.OK, """{"ok":true}""");
                }

                if (request.Method == HttpMethod.Post && path == "/v2/document/complete/up-compress")
                {
                    return GzipJsonResponse(HttpStatusCode.BadRequest, """{"error":"finalize failed"}""");
                }

                return JsonResponse(HttpStatusCode.BadRequest, """{"error":"unexpected request"}""");
            });

            var service = CreateDirectUploadService(handler, dbPath);
            var plan = CreatePlan(filePath, useMultipartUpload: true);
            var context = new DirectUploadPlanContext
            {
                MaxConcurrency = 1,
                MaxRetryAttempts = 1,
                MultipartChunkSizeBytes = 1024 * 1024
            };

            var result = await service.UploadAsync(plan, context, cancellationToken: CancellationToken.None);

            Assert.False(result.Files[0].Succeeded);
            Assert.Equal(400, result.Files[0].HttpStatus);
            Assert.Contains("finalize failed", result.Files[0].Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static UploadPlanResult CreatePlan(
        string filePath,
        bool useMultipartUpload = false,
        string destinationContainerId = "D-DESTINATION")
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
                    DestinationContainerId: destinationContainerId,
                    ProfileValues: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Acl: null,
                    UseMultipartUpload: useMultipartUpload)
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

    private static NetDocumentsDirectUploadService CreateDirectUploadService(
        HttpMessageHandler handler,
        string dbPath,
        string apiBaseUrl = "https://api.au.netdocuments.com")
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
            () => apiBaseUrl,
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

    private static HttpResponseMessage HtmlResponse(HttpStatusCode status, string payload)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/html")
        };
    }

    private static HttpResponseMessage GzipJsonResponse(HttpStatusCode status, string payload)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(payloadBytes, 0, payloadBytes.Length);
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");

        return new HttpResponseMessage(status)
        {
            Content = content
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

    private static string? ReadFormUrlEncodedValue(string form, string key)
    {
        if (string.IsNullOrWhiteSpace(form) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (var pair in form.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var encodedKey = pair[..separatorIndex];
            if (!string.Equals(Uri.UnescapeDataString(encodedKey.Replace('+', ' ')), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var encodedValue = pair[(separatorIndex + 1)..];
            return Uri.UnescapeDataString(encodedValue.Replace('+', ' '));
        }

        return null;
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
