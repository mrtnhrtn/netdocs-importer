using System.Net;
using System.Text;
using System.Text.Json;
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
    public async Task EnumerateExportScopesAsync_NormalizesRecentWorkspaceIdBeforeEnumerating()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-scope-normalize-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");

        try
        {
            const string numericWorkspaceId = "3470-5201-2062";
            const string envWorkspaceId = ":AU2:3:u:l:a:^W221010143616611.nev";

            var handler = new StubHttpHandler(request =>
            {
                if (request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.BadRequest, """{"error":"missing uri"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var unescapedPath = Uri.UnescapeDataString(path);

                if (string.Equals(unescapedPath, $"/v2/container/{numericWorkspaceId}/info", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, $$"""
                        {
                          "data": {
                            "id": "{{envWorkspaceId}}",
                            "description": "Workspace Root",
                            "extension": "ndws"
                          }
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains($"container={Uri.UnescapeDataString(envWorkspaceId)}", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("extension eq 'ndfld'", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "standardList": [
                            {
                              "standardAttributes": {
                                "id": "FLD-1",
                                "description": "General",
                                "Ext": "ndfld"
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
                Id = numericWorkspaceId,
                Name = "Workspace Root",
                Extension = "ndws"
            };

            var scopes = await service.EnumerateExportScopesAsync("NG-CAB", root, includeWorkspaceFilters: true);

            Assert.Equal(2, scopes.Count);
            Assert.Contains(scopes, scope => scope.ContainerId == envWorkspaceId);
            Assert.Contains(scopes, scope => scope.ContainerId == "FLD-1");
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
    public async Task EnumerateContainerDocumentsAsync_SkipsFolderRowsEvenWhenTheyExposeDocumentIdentifiers()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-doc-folder-skip-tests-{Guid.NewGuid():N}");
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
                    query.Contains("container=ROOT", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "standardList": [
                            {
                              "standardAttributes": {
                                "docId": "FLD-1",
                                "description": "Invoices",
                                "Ext": "ndfld",
                                "type": "ndfld"
                              }
                            },
                            {
                              "standardAttributes": {
                                "docId": "DOC-1",
                                "description": "Invoice_1",
                                "extension": "pdf",
                                "type": "document"
                              }
                            }
                          ]
                        }
                        """);
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

            var documents = await service.EnumerateContainerDocumentsAsync("NG-CAB", scope);

            var document = Assert.Single(documents);
            Assert.Equal("DOC-1", document.DocumentId);
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
    public async Task EnumerateExportScopesAsync_UsesWorkspaceEndpointToEnumerateLargeChildSets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-scope-page-tests-{Guid.NewGuid():N}");
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

                var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
                if (string.Equals(path, "/v1/Workspace/ROOT", StringComparison.OrdinalIgnoreCase))
                {
                    var children = Enumerable.Range(1, 250)
                        .Select(index => new Dictionary<string, object?>
                        {
                            ["id"] = $":AU2:3:u:l:a:^F{index}.nev",
                            ["description"] = $"Folder {index}",
                            ["extension"] = "ndfld",
                            ["type"] = "ndfld",
                            ["workspaceId"] = "ROOT"
                        });
                    return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = children }));
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

            var scopes = await service.EnumerateExportScopesAsync("NG-CAB", root, includeWorkspaceFilters: true);

            Assert.Equal(251, scopes.Count);
            Assert.Contains(scopes, scope => scope.ContainerId == ":AU2:3:u:l:a:^F250.nev");
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
    public async Task EnumerateContainerDocumentsAsync_ReadsSelectedCustomAttributeFieldsFromTopLevelProperties()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-doc-custom-select-tests-{Guid.NewGuid():N}");
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
                                "docId": "DOC-100",
                                "description": "Matter Summary.pdf",
                                "sizeBytes": 12345
                              },
                              "1001": "CLIENT-01",
                              "1002": "MATTER-99"
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

            var documents = await service.EnumerateContainerDocumentsAsync("NG-CAB", scope, customAttributeIds: new[] { "1001", "1002" });

            var document = Assert.Single(documents);
            Assert.Contains(document.CustomAttributes, attribute => attribute.Name == "custom.1001" && attribute.Value == "CLIENT-01");
            Assert.Contains(document.CustomAttributes, attribute => attribute.Name == "custom.1002" && attribute.Value == "MATTER-99");
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
    public async Task EnumerateContainerDocumentsAsync_ReadsVersionHintsFromVersionsLiteArray()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-doc-version-hints-tests-{Guid.NewGuid():N}");
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
                                "docId": "DOC-200",
                                "description": "Agreement.docx",
                                "sizeBytes": 2048
                              },
                              "versionsLite": [
                                { "versionId": "1", "official": false, "sizeBytes": 1024 },
                                { "versionId": "2", "official": true, "sizeBytes": 2048 }
                              ]
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

            var documents = await service.EnumerateContainerDocumentsAsync("NG-CAB", scope);

            var document = Assert.Single(documents);
            Assert.Equal("2", document.OfficialVersionId);
            Assert.Equal(2, document.VersionHints.Count);
            Assert.Contains(document.VersionHints, version => version.VersionId == "1");
            Assert.Contains(document.VersionHints, version => version.VersionId == "2" && version.IsOfficial);
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
    public async Task EnumerateContainerDocumentsAsync_UsesLeanPreflightQueryWithoutValidateWorkspaces()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-doc-query-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");

        try
        {
            string? firstQuery = null;
            var requestPaths = new List<string>();
            var handler = new StubHttpHandler(request =>
            {
                if (request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.BadRequest, """{"error":"missing uri"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                requestPaths.Add(request.RequestUri.PathAndQuery);
                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase))
                {
                    firstQuery ??= Uri.UnescapeDataString(request.RequestUri.Query);
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

            var documents = await service.EnumerateContainerDocumentsAsync("NG-CAB", scope);

            Assert.Empty(documents);
            Assert.NotNull(firstQuery);
            Assert.Contains("select=StandardAttributes,VersionsLite,ByteSize", firstQuery, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("listflags=Documents,ByteSize", firstQuery, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ValidateWorkspaces", firstQuery, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Descriptions", firstQuery, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DispNames", firstQuery, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                requestPaths,
                path => path.Contains("/v2/container/", StringComparison.OrdinalIgnoreCase) &&
                        path.Contains("/search?", StringComparison.OrdinalIgnoreCase));
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
    public async Task EnumerateContainerDocumentsAsync_StopsWhenPaginationStallsWithRepeatedRows()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-doc-stall-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");

        try
        {
            var requestCount = 0;
            var repeatedPagePayload = JsonSerializer.Serialize(new
            {
                standardList = Enumerable.Range(1, 200)
                    .Select(index => new
                    {
                        standardAttributes = new
                        {
                            docId = $"DOC-{index}",
                            description = $"Document {index}.docx",
                            sizeBytes = 1000 + index
                        }
                    })
                    .ToArray()
            });
            var handler = new StubHttpHandler(request =>
            {
                if (request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.BadRequest, """{"error":"missing uri"}""");
                }

                var path = request.RequestUri.AbsolutePath;
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=ROOT", StringComparison.OrdinalIgnoreCase))
                {
                    requestCount++;
                    return JsonResponse(HttpStatusCode.OK, repeatedPagePayload);
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

            var documents = await service.EnumerateContainerDocumentsAsync("NG-CAB", scope);

            Assert.Equal(200, documents.Count);
            Assert.Equal(3, requestCount);
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
    public async Task EnumerateContainerDocumentsAsync_UsesNextOffsetFromResponse()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-doc-nextoffset-tests-{Guid.NewGuid():N}");
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
                          "nextOffset": 500,
                          "standardList": [
                            {
                              "standardAttributes": {
                                "docId": "DOC-1",
                                "description": "Document 1.docx",
                                "sizeBytes": 1001
                              }
                            }
                          ]
                        }
                        """);
                }

                if (string.Equals(path, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("container=ROOT", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("skip=500", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "standardList": [
                            {
                              "standardAttributes": {
                                "docId": "DOC-2",
                                "description": "Document 2.docx",
                                "sizeBytes": 1002
                              }
                            }
                          ]
                        }
                        """);
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

            var documents = await service.EnumerateContainerDocumentsAsync("NG-CAB", scope);

            Assert.Equal(2, documents.Count);
            Assert.Contains(documents, document => document.DocumentId == "DOC-1");
            Assert.Contains(documents, document => document.DocumentId == "DOC-2");
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
    public async Task EnumerateContainerDocumentsAsync_UsesSkipTokenFromSearchResponse()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-doc-skiptoken-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");

        try
        {
            var requestQueries = new List<string>();
            var handler = new StubHttpHandler(request =>
            {
                if (request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.BadRequest, """{"error":"missing uri"}""");
                }

                if (request.Method == HttpMethod.Get &&
                    string.Equals(request.RequestUri.AbsolutePath, "/v2/search/NG-CAB", StringComparison.OrdinalIgnoreCase))
                {
                    var query = Uri.UnescapeDataString(request.RequestUri.Query);
                    requestQueries.Add(query);
                    var hasSkipToken = query.Contains("skiptoken=", StringComparison.OrdinalIgnoreCase);

                    if (!hasSkipToken)
                    {
                        return JsonResponse(HttpStatusCode.OK, """
                        {
                          "skipToken": "TOKEN-2",
                          "standardList": [
                            {
                              "standardAttributes": {
                                "docId": "DOC-1",
                                "description": "Document 1.docx",
                                "sizeBytes": 1001
                              }
                            }
                          ]
                        }
                        """);
                    }

                    if (query.Contains("skiptoken=TOKEN-2", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponse(HttpStatusCode.OK, """
                        {
                          "standardList": [
                            {
                              "standardAttributes": {
                                "docId": "DOC-2",
                                "description": "Document 2.docx",
                                "sizeBytes": 1002
                              }
                            }
                          ]
                        }
                        """);
                    }
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

            var documents = await service.EnumerateContainerDocumentsAsync("NG-CAB", scope);

            Assert.Equal(2, documents.Count);
            Assert.Equal(2, requestQueries.Count);
            Assert.Contains("container=ROOT", requestQueries[0], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("skiptoken=", requestQueries[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("skiptoken=TOKEN-2", requestQueries[1], StringComparison.OrdinalIgnoreCase);
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
    public async Task EnumerateDocumentVersionsAsync_ReturnsEmptyWithoutCallingUndocumentedEndpoints()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-version-disabled-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");

        try
        {
            var requestCount = 0;
            var handler = new StubHttpHandler(request =>
            {
                requestCount++;
                return JsonResponse(HttpStatusCode.BadRequest, """{"error":"unexpected"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var versions = await service.EnumerateDocumentVersionsAsync("DOC-1");

            Assert.Empty(versions);
            Assert.Equal(0, requestCount);
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
    public async Task EnumerateDocumentVersionsAsync_RemainsNoOpAcrossRepeatedCalls()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-version-noop-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");

        try
        {
            var requestCount = 0;
            var handler = new StubHttpHandler(request =>
            {
                requestCount++;
                return JsonResponse(HttpStatusCode.BadRequest, """{"error":"malformed"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            await service.EnumerateDocumentVersionsAsync("DOC-1");
            await service.EnumerateDocumentVersionsAsync("DOC-2");
            await service.EnumerateDocumentVersionsAsync("DOC-3");
            await service.EnumerateDocumentVersionsAsync("DOC-4");

            Assert.Equal(0, requestCount);
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
    public async Task GetDocumentStandardAttributesForExportRunAsync_ReadsV1DocumentAttributes()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-run-meta-tests-{Guid.NewGuid():N}");
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

                var path = request.RequestUri.PathAndQuery;
                if (string.Equals(path, "/v1/Document/DOC-1/2", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "standardAttributes": {
                            "docId": "DOC-1",
                            "description": "Project Plan v2.docx",
                            "extension": "docx"
                          }
                        }
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var attributes = await service.GetDocumentStandardAttributesForExportRunAsync("DOC-1", "2");

            Assert.Contains(attributes, attribute => attribute.Name == "standard.docId" && attribute.Value == "DOC-1");
            Assert.Contains(attributes, attribute => attribute.Name == "standard.description" && attribute.Value == "Project Plan v2.docx");
            Assert.Contains(attributes, attribute => attribute.Name == "standard.extension" && attribute.Value == "docx");
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
    public async Task GetDocumentStandardAttributesForExportRunAsync_FallsBackToStandardAttributesQueryVariant()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nd-export-run-meta-fallback-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobstore.db");

        try
        {
            var requestPaths = new List<string>();
            var handler = new StubHttpHandler(request =>
            {
                if (request.RequestUri is null)
                {
                    return JsonResponse(HttpStatusCode.BadRequest, """{"error":"missing uri"}""");
                }

                requestPaths.Add(request.RequestUri.PathAndQuery);
                if (string.Equals(request.RequestUri.PathAndQuery, "/v1/Document/DOC-2?standardattributes=true", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                          "standardAttributes": {
                            "docId": "DOC-2",
                            "description": "Cover Letter.pdf"
                          }
                        }
                        """);
                }

                return JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""");
            });

            var service = CreateSyncService(handler, dbPath);
            var attributes = await service.GetDocumentStandardAttributesForExportRunAsync("DOC-2");

            Assert.Contains("/v1/Document/DOC-2", requestPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("/v1/Document/DOC-2?standardattributes=true", requestPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(attributes, attribute => attribute.Name == "standard.docId" && attribute.Value == "DOC-2");
            Assert.Contains(attributes, attribute => attribute.Name == "standard.description" && attribute.Value == "Cover Letter.pdf");
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
