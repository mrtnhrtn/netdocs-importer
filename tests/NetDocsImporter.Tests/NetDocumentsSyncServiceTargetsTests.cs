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
    public async Task GetContainerChildrenAsync_PrefersCollabspaceEnvIdWhenNumericIdIsAlsoPresent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        const string collabEnvId = ":AU2:q:k:y:e:^C230328184925370.nev";

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.Method != HttpMethod.Get || request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"error":"method not allowed"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=WS-1", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("ndfld", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, $$"""
                        {
                          "standardList": [
                            {
                              "standardAttributes": {
                                "id": "3446-9140-6114",
                                "envId": "{{collabEnvId}}",
                                "description": "Quest",
                                "Ext": "ndfld",
                                "workspaceId": "WS-1"
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

                if (string.Equals(path, "/v2/search", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardList": []}""");
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var nodes = await service.GetContainerChildrenAsync("NG-CAB", parentContainerId: "WS-1");

            var node = Assert.Single(nodes);
            Assert.Equal(collabEnvId, node.Id);
            Assert.Equal("Quest", node.Name);
            Assert.Equal(NdTargetType.Folder, node.SupportedType);
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
    public async Task GetContainerChildrenAsync_DoesNotEmitInvalidStructuredFolderContainerVariants()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        var requests = new List<Uri>();
        const string folderId = ":AU2:a:6:q:2:^F251003112737910.nev|1";

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.Method != HttpMethod.Get || request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"error":"method not allowed"}""");
                }

                lock (requests)
                {
                    requests.Add(request.RequestUri);
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (string.Equals(path, "/v2/container/:AU2:a:6:q:2:^F251003112737910.nev/info", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": {
                            "id": ":AU2:a:6:q:2:^F251003112737910.nev",
                            "description": "Invoices",
                            "extension": "ndfld"
                          }
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=:AU2:a:6:q:2:^F251003112737910.nev", StringComparison.OrdinalIgnoreCase))
                {
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
                parentContainerId: folderId,
                preferredType: NdTargetType.Folder);

            Assert.Empty(nodes);

            var searchRequests = requests
                .Where(uri => uri.AbsolutePath.StartsWith("/v2/search", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.NotEmpty(searchRequests);
            Assert.DoesNotContain(
                searchRequests,
                uri => Uri.UnescapeDataString(uri.Query).Contains(".nev|1.nev", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                searchRequests,
                uri => Uri.UnescapeDataString(uri.Query).Contains("container=^:", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public async Task GetContainerChildrenAsync_HydratesIdLikeFolderNamesFromContainerInfo()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        const string folderId = ":AU2:a:6:q:2:^F251003112737910.nev";

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.Method != HttpMethod.Get || request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"error":"method not allowed"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var unescapedPath = Uri.UnescapeDataString(path);
                if (string.Equals(path, "/v2/container/WS-1/info", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": {
                            "id": "WS-1",
                            "description": "Workspace Root",
                            "extension": "ndws"
                          }
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=WS-1", StringComparison.OrdinalIgnoreCase))
                {
                    if (query.Contains("ndfld", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponse(HttpStatusCode.OK, $$"""
                            {
                              "standardList": [
                                {
                                  "standardAttributes": {
                                    "id": "{{folderId}}",
                                    "description": "{{folderId}}",
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

                if (string.Equals(path, "/v2/search", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardList": []}""");
                }

                if (unescapedPath.StartsWith("/v2/container/", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.EndsWith("/info", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.Contains(folderId, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": {
                            "id": ":AU2:a:6:q:2:^F251003112737910.nev",
                            "extension": "ndfld",
                            "dispNames": {
                              "long": "Client Intake"
                            }
                          }
                        }
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var nodes = await service.GetContainerChildrenAsync("NG-CAB", parentContainerId: "WS-1", workspaceId: "WS-1");

            var folder = Assert.Single(nodes);
            Assert.Equal(folderId, folder.Id);
            Assert.Equal("Client Intake", folder.Name);
            Assert.Equal(NdTargetType.Folder, folder.SupportedType);
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
    public async Task GetContainerChildrenAsync_HydratesNevLikeNamesEvenWhenNotExactlyEqualToId()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        const string folderId = ":AU2:a:6:q:2:^F251003112737910.nev|1";
        const string nevLikeName = ":AU2:a:6:q:2:^F251003112737910.nev";

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.Method != HttpMethod.Get || request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"error":"method not allowed"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var unescapedPath = Uri.UnescapeDataString(path);
                if (string.Equals(path, "/v2/container/WS-1/info", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": {
                            "id": "WS-1",
                            "description": "Workspace Root",
                            "extension": "ndws"
                          }
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=WS-1", StringComparison.OrdinalIgnoreCase))
                {
                    if (query.Contains("ndfld", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponse(HttpStatusCode.OK, $$"""
                            {
                              "standardList": [
                                {
                                  "standardAttributes": {
                                    "id": "{{folderId}}",
                                    "description": "{{nevLikeName}}",
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

                if (string.Equals(path, "/v2/search", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardList": []}""");
                }

                if (unescapedPath.StartsWith("/v2/container/", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.EndsWith("/info", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.Contains(":AU2:a:6:q:2:^F251003112737910.nev", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": {
                            "id": ":AU2:a:6:q:2:^F251003112737910.nev|1",
                            "extension": "ndfld",
                            "dispNames": {
                              "long": "Discovery"
                            }
                          }
                        }
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var nodes = await service.GetContainerChildrenAsync("NG-CAB", parentContainerId: "WS-1", workspaceId: "WS-1");

            var folder = Assert.Single(nodes);
            Assert.Equal(folderId, folder.Id);
            Assert.Equal("Discovery", folder.Name);
            Assert.Equal(NdTargetType.Folder, folder.SupportedType);
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
    public async Task GetTargetProfileSnapshotAsync_ResolvesDefaultsFromNumericContainerAttributeKeys()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");

        try
        {
            var store = new JobStore(dbPath);
            await store.InitializeAsync();
            var syncedUtc = DateTime.UtcNow;
            await store.ReplaceNetDocumentsAttributesAsync(
                "NG-CAB",
                new[]
                {
                    new NetDocumentsAttributeRecord("NG-CAB", "NG-REPO", 2, "ATTR_CLIENT", "Client", "lookup", true, false, true, null, false, syncedUtc),
                    new NetDocumentsAttributeRecord("NG-CAB", "NG-REPO", 3, "ATTR_MATTER", "Matter", "lookup", true, false, true, 2, true, syncedUtc)
                });

            var handler = new StubHttpHandler(request =>
            {
                if (request.Method != HttpMethod.Get || request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"error":"method not allowed"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (path.StartsWith("/v1/Container/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/v1/Containers/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/v1/Workspace/", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
                }

                if (string.Equals(path, "/v2/container/FOLDER-1/info", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("select=", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": {
                            "id": "FOLDER-1",
                            "extension": "ndfld",
                            "customAttributes": {
                              "2": {
                                "key": "0000",
                                "description": "Martin's Client"
                              },
                              "3": {
                                "key": "011",
                                "description": "Demo Matter"
                              }
                            }
                          }
                        }
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var snapshot = await service.GetTargetProfileSnapshotAsync(
                "NG-CAB",
                "NG-REPO",
                new NdTargetSelection
                {
                    Id = "FOLDER-1",
                    Name = "Folder 1",
                    Type = NdTargetType.Folder
                });

            Assert.Equal(2, snapshot.EffectiveDefaults.ValuesByAttributeId.Count);
            Assert.True(snapshot.EffectiveDefaults.ValuesByAttributeId.TryGetValue("ATTR_CLIENT", out var client));
            Assert.Equal("Client", client!.AttributeName);
            Assert.Equal("0000", client.RawValue);
            Assert.Equal("Martin's Client", client.DisplayValue);
            Assert.True(snapshot.EffectiveDefaults.ValuesByAttributeId.TryGetValue("ATTR_MATTER", out var matter));
            Assert.Equal("Matter", matter!.AttributeName);
            Assert.Equal("011", matter.RawValue);
            Assert.Equal("Demo Matter", matter.DisplayValue);
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
    public async Task GetRecentTargetsAsync_HydratesCollabspaceNevNameFromContainerInfo()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        const string collabspaceId = ":AU2:l:6:f:q:^C250116181506583.nev";

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.Method != HttpMethod.Get || request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"error":"method not allowed"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var unescapedPath = Uri.UnescapeDataString(path);
                if (string.Equals(path, "/v1/User/wsRecent", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, $$"""
                        {
                          "standardList": [
                            {
                              "standardAttributes": {
                                "id": "{{collabspaceId}}",
                                "description": "{{collabspaceId}}",
                                "Ext": "ndfld"
                              }
                            }
                          ]
                        }
                        """);
                }

                if (unescapedPath.StartsWith("/v2/container/", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.EndsWith("/info", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.Contains(collabspaceId, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": {
                            "id": ":AU2:l:6:f:q:^C250116181506583.nev",
                            "extension": "ndfld",
                            "dispNames": {
                              "long": "External Share - Opp Counsel"
                            }
                          }
                        }
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var recents = await service.GetRecentTargetsAsync(cabinetId: "NG-CAB");

            var item = Assert.Single(recents);
            Assert.Equal(collabspaceId, item.Selection.Id);
            Assert.Equal(NdTargetType.Folder, item.Selection.Type);
            Assert.Equal("External Share - Opp Counsel", item.Selection.Name);
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
    public async Task GetRecentTargetsAsync_RewritesNumericCollabspaceIdToEnvIdFromHydration()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        const string collabEnvId = ":AU2:q:k:y:e:^C230328184925370.nev";

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.Method != HttpMethod.Get || request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"error":"method not allowed"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var unescapedPath = Uri.UnescapeDataString(path);
                if (string.Equals(path, "/v1/User/wsRecent", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "standardList": [
                            {
                              "standardAttributes": {
                                "id": "3446-9140-6114",
                                "description": "3446-9140-6114",
                                "Ext": "ndfld"
                              }
                            }
                          ]
                        }
                        """);
                }

                if (unescapedPath.StartsWith("/v2/container/3446-9140-6114/info", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, $$"""
                        {
                          "data": {
                            "id": "3446-9140-6114",
                            "envId": "{{collabEnvId}}",
                            "extension": "ndfld",
                            "description": "Quest"
                          }
                        }
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var recents = await service.GetRecentTargetsAsync(cabinetId: "NG-CAB");

            var item = Assert.Single(recents);
            Assert.Equal(collabEnvId, item.Selection.Id);
            Assert.Equal("Quest", item.Selection.Name);
            Assert.Equal(NdTargetType.Folder, item.Selection.Type);
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
    public async Task SearchWorkspacesAsync_HydratesNevLikeWorkspaceNames()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        const string workspaceId = ":AU2:o:w:m:v:^W200423132232851.nev";

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.Method != HttpMethod.Get || request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"error":"method not allowed"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var unescapedPath = Uri.UnescapeDataString(path);
                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("extension eq 'ndws'", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, $$"""
                        {
                          "standardList": [
                            {
                              "standardAttributes": {
                                "id": "{{workspaceId}}",
                                "description": "{{workspaceId}}",
                                "Ext": "ndws"
                              }
                            }
                          ]
                        }
                        """);
                }

                if (unescapedPath.StartsWith("/v2/container/", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.EndsWith("/info", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.Contains(workspaceId, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": {
                            "id": ":AU2:o:w:m:v:^W200423132232851.nev",
                            "extension": "ndws",
                            "dispNames": {
                              "long": "0000.011 - Demo Workspace - Martin's Client"
                            }
                          }
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """{"standardList": []}""");
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var results = await service.SearchWorkspacesAsync("NG-CAB", "011");

            var workspace = Assert.Single(results);
            Assert.Equal(workspaceId, workspace.WorkspaceId);
            Assert.Equal("0000.011 - Demo Workspace - Martin's Client", workspace.WorkspaceName);
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
    public async Task GetContainerChildrenAsync_CollabspaceFallsBackToAncestryWhenInfoNameIsNev()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        const string collabId = ":AU2:l:6:f:q:^C250116181506583.nev";

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.Method != HttpMethod.Get || request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"error":"method not allowed"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var unescapedPath = Uri.UnescapeDataString(path);

                if (string.Equals(path, "/v2/container/WS-1/info", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": {
                            "id": "WS-1",
                            "description": "Workspace Root",
                            "extension": "ndws"
                          }
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=WS-1", StringComparison.OrdinalIgnoreCase))
                {
                    if (query.Contains("ndfld", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponse(HttpStatusCode.OK, $$"""
                            {
                              "standardList": [
                                {
                                  "standardAttributes": {
                                    "id": "{{collabId}}",
                                    "description": "{{collabId}}",
                                    "Ext": "ndfld"
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

                if (unescapedPath.StartsWith("/v2/container/", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.EndsWith("/info", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.Contains(collabId, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, $$"""
                        {
                          "data": {
                            "id": "{{collabId}}",
                            "description": "{{collabId}}",
                            "extension": "ndfld"
                          }
                        }
                        """);
                }

                if (unescapedPath.StartsWith("/v2/container/", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.EndsWith("/ancestry", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.Contains(collabId, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        [
                          { "name": "0000.011 - Demo Workspace - Martin's Client" },
                          { "name": "External Share - Opp Counsel" }
                        ]
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var nodes = await service.GetContainerChildrenAsync("NG-CAB", parentContainerId: "WS-1", workspaceId: "WS-1");

            var collab = Assert.Single(nodes);
            Assert.Equal(collabId, collab.Id);
            Assert.Equal("External Share - Opp Counsel", collab.Name);
            Assert.Equal(NdTargetType.Folder, collab.SupportedType);
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
    public async Task GetContainerChildrenAsync_IdLikeTildeFolderFallsBackToAncestryName()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        const string folderId = ":AU2:u:3:e:p:~260209191130366.nev";

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.Method != HttpMethod.Get || request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"error":"method not allowed"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var unescapedPath = Uri.UnescapeDataString(path);

                if (string.Equals(path, "/v2/container/WS-1/info", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": {
                            "id": "WS-1",
                            "description": "Workspace Root",
                            "extension": "ndws"
                          }
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=WS-1", StringComparison.OrdinalIgnoreCase))
                {
                    if (query.Contains("ndfld", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponse(HttpStatusCode.OK, $$"""
                            {
                              "standardList": [
                                {
                                  "standardAttributes": {
                                    "id": "{{folderId}}",
                                    "description": "{{folderId}}",
                                    "Ext": "ndfld"
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

                if (unescapedPath.StartsWith("/v2/container/", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.EndsWith("/info", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.Contains(folderId, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, $$"""
                        {
                          "data": {
                            "id": "{{folderId}}",
                            "description": "{{folderId}}",
                            "extension": "ndfld"
                          }
                        }
                        """);
                }

                if (unescapedPath.StartsWith("/v2/container/", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.EndsWith("/ancestry", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.Contains(folderId, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        [
                          { "name": "0000.011 - Demo Workspace - Martin's Client" },
                          { "name": "General Correspondence" }
                        ]
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var nodes = await service.GetContainerChildrenAsync("NG-CAB", parentContainerId: "WS-1", workspaceId: "WS-1");

            var folder = Assert.Single(nodes);
            Assert.Equal(folderId, folder.Id);
            Assert.Equal("General Correspondence", folder.Name);
            Assert.Equal(NdTargetType.Folder, folder.SupportedType);
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
    public async Task GetContainerChildrenAsync_UsesCallerTypeWhenContainerInfoOmitsType()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");
        const string listedId = ":AU2:7:6:x:k:^C240321180124474.nev";
        const string hydratedId = ":AU2:u:3:e:p:~260209191130366.nev";

        try
        {
            var handler = new StubHttpHandler(request =>
            {
                if (request.Method != HttpMethod.Get || request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"error":"method not allowed"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var unescapedPath = Uri.UnescapeDataString(path);

                if (string.Equals(path, "/v2/container/WS-1/info", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "data": {
                            "id": "WS-1",
                            "description": "Workspace Root",
                            "extension": "ndws"
                          }
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=WS-1", StringComparison.OrdinalIgnoreCase))
                {
                    if (query.Contains("ndfld", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponse(HttpStatusCode.OK, $$"""
                            {
                              "standardList": [
                                {
                                  "standardAttributes": {
                                    "id": "{{listedId}}",
                                    "description": "{{listedId}}",
                                    "Ext": "ndfld"
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

                if (unescapedPath.StartsWith("/v2/container/", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.EndsWith("/info", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.Contains(listedId, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, $$"""
                        {
                          "data": {
                            "id": "{{hydratedId}}",
                            "description": "{{hydratedId}}"
                          }
                        }
                        """);
                }

                if (unescapedPath.StartsWith("/v2/container/", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.EndsWith("/ancestry", StringComparison.OrdinalIgnoreCase) &&
                    unescapedPath.Contains(hydratedId, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        [
                          { "name": "0000.011 - Demo Workspace - Martin's Client" },
                          { "name": "External Share - Opp Counsel" }
                        ]
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var nodes = await service.GetContainerChildrenAsync("NG-CAB", parentContainerId: "WS-1", workspaceId: "WS-1");

            var node = Assert.Single(nodes);
            Assert.Equal(listedId, node.Id);
            Assert.Equal(NdTargetType.Folder, node.SupportedType);
            Assert.Equal("External Share - Opp Counsel", node.Name);
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
