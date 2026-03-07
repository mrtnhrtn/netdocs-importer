using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using NetDocsImporter.Core;

namespace NetDocsImporter.NetDocs;

public sealed partial class NetDocumentsSyncService
{

    public async Task<NdExportScopeEnumerationResult> EnumerateExportScopesAsync(
        string cabinetId,
        NdTargetSelection root,
        bool includeWorkspaceFilters,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cabinetId))
        {
            throw new ArgumentException("Cabinet id is required.", nameof(cabinetId));
        }

        if (root is null || string.IsNullOrWhiteSpace(root.Id))
        {
            throw new ArgumentException("Root selection is required.", nameof(root));
        }

        var result = new List<NdExportScope>();
        var issues = new List<NdExportScopeTraversalIssue>();
        var queue = new Queue<NdExportScope>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var resolvedRoot = new NdTargetSelection
        {
            Type = root.Type,
            Id = await ResolveContainerIdForBrowseAsync(root.Id, cancellationToken),
            Name = root.Name,
            ParentWorkspaceId = root.ParentWorkspaceId,
            Extension = root.Extension,
            SourceFlow = root.SourceFlow
        };

        var rootScope = BuildScopeFromSelection(resolvedRoot, parentContainerId: null, pathSegments: new[]
        {
            string.IsNullOrWhiteSpace(resolvedRoot.Name) ? resolvedRoot.Id : resolvedRoot.Name
        });
        queue.Enqueue(rootScope);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scope = queue.Dequeue();
            var dedupeKey = BuildScopeDedupeKey(scope.TargetType, scope.ContainerId);
            if (!visited.Add(dedupeKey))
            {
                continue;
            }

            var isWorkspaceFilter = scope.TargetType == NdTargetType.WorkspaceFilter;
            var includeScope = !isWorkspaceFilter ||
                               includeWorkspaceFilters ||
                               scope.Kind == NdExportScopeKind.SavedSearch;
            if (includeScope)
            {
                result.Add(scope);
            }

            if (scope.TargetType == NdTargetType.WorkspaceFilter)
            {
                continue;
            }

            IReadOnlyList<NdContainerNode> children;
            try
            {
                children = await GetContainerChildrenAsync(
                    cabinetId,
                    parentContainerId: scope.ContainerId,
                    preferredType: scope.TargetType,
                    throwOnFailure: true,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                issues.Add(new NdExportScopeTraversalIssue
                {
                    ContainerId = scope.ContainerId,
                    ScopeName = scope.Name,
                    TargetType = scope.TargetType,
                    Message = ex.Message
                });
                continue;
            }

            foreach (var child in children)
            {
                if (child.SupportedType is null || string.IsNullOrWhiteSpace(child.Id))
                {
                    continue;
                }

                var childSelection = new NdTargetSelection
                {
                    Type = child.SupportedType.Value,
                    Id = child.Id,
                    Name = string.IsNullOrWhiteSpace(child.Name) ? child.Id : child.Name,
                    ParentWorkspaceId = child.ParentWorkspaceId,
                    Extension = child.Extension,
                    SourceFlow = NdTargetSourceFlow.Browse
                };

                var childSegments = new List<string>(scope.PathSegments);
                childSegments.Add(childSelection.Name);
                queue.Enqueue(BuildScopeFromSelection(childSelection, scope.ContainerId, childSegments));
            }
        }

        return new NdExportScopeEnumerationResult
        {
            Scopes = result,
            Issues = issues
        };
    }

    public async Task<IReadOnlyList<NdExportDocument>> EnumerateContainerDocumentsAsync(
        string cabinetId,
        NdExportScope scope,
        IReadOnlyList<string>? customAttributeIds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cabinetId))
        {
            throw new ArgumentException("Cabinet id is required.", nameof(cabinetId));
        }

        if (scope is null || string.IsNullOrWhiteSpace(scope.ContainerId))
        {
            throw new ArgumentException("Container scope is required.", nameof(scope));
        }

        var documents = new Dictionary<string, NdExportDocument>(StringComparer.OrdinalIgnoreCase);
        const int pageSize = 200;
        var skip = 0;
        string? skipToken = null;
        var repeatedNoProgressPages = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageResult = await QueryContainerDocumentPageAsync(
                cabinetId,
                scope.ContainerId,
                pageSize,
                skip,
                skipToken,
                customAttributeIds,
                cancellationToken);
            var page = pageResult.Items;
            if (page.Count == 0)
            {
                break;
            }

            var addedThisPage = 0;
            foreach (var row in page)
            {
                if (!documents.ContainsKey(row.DocumentId))
                {
                    documents[row.DocumentId] = row;
                    addedThisPage++;
                }
            }

            if (addedThisPage == 0)
            {
                repeatedNoProgressPages++;
                if (repeatedNoProgressPages >= 2)
                {
                    Trace.WriteLine(
                        $"ND-EXPORT pagination stalled for container='{scope.ContainerId}' skip={skip} pageSize={page.Count}. Ending enumeration to avoid repeated requests.");
                    break;
                }
            }
            else
            {
                repeatedNoProgressPages = 0;
            }

            if (!string.IsNullOrWhiteSpace(pageResult.NextSkipToken) &&
                !string.Equals(pageResult.NextSkipToken, skipToken, StringComparison.Ordinal))
            {
                skipToken = pageResult.NextSkipToken;
                continue;
            }

            if (pageResult.NextOffset.HasValue && pageResult.NextOffset.Value > skip)
            {
                skip = pageResult.NextOffset.Value;
                continue;
            }

            if (page.Count < pageSize)
            {
                break;
            }

            skip += pageSize;
        }

        return documents.Values.ToList();
    }

    public async Task<IReadOnlyList<NdExportDocumentVersion>> EnumerateDocumentVersionsAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        return Array.Empty<NdExportDocumentVersion>();
    }

    public async Task<IReadOnlyList<NdExportAttributeValue>> GetDocumentStandardAttributesForExportRunAsync(
        string documentId,
        string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return Array.Empty<NdExportAttributeValue>();
        }

        // TODO(export-run): use this in the binary download pipeline to enrich run-phase metadata from v1/Document.
        var encodedDocumentId = Uri.EscapeDataString(documentId);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            var encodedVersionId = Uri.EscapeDataString(versionId);
            candidates.Add($"/v1/Document/{encodedDocumentId}/{encodedVersionId}");
            candidates.Add($"/v1/Document/{encodedDocumentId}/{encodedVersionId}?standardattributes=true");
        }
        else
        {
            candidates.Add($"/v1/Document/{encodedDocumentId}");
            candidates.Add($"/v1/Document/{encodedDocumentId}?standardattributes=true");
        }

        foreach (var path in candidates)
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                var attributes = ReadStandardAttributes(document.RootElement);
                if (attributes.Count > 0)
                {
                    return attributes;
                }
            }
            catch
            {
                // Try the next endpoint variant.
            }
        }

        return Array.Empty<NdExportAttributeValue>();
    }

    public async Task<NdBinaryDownloadResponse> DownloadDocumentBinaryForExportRunAsync(
        string documentId,
        string destinationFilePath,
        string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return new NdBinaryDownloadResponse
            {
                Succeeded = false,
                StatusCode = 0,
                ErrorMessage = "Document id is required."
            };
        }

        if (string.IsNullOrWhiteSpace(destinationFilePath))
        {
            return new NdBinaryDownloadResponse
            {
                Succeeded = false,
                StatusCode = 0,
                ErrorMessage = "Destination file path is required."
            };
        }

        var encodedDocumentId = Uri.EscapeDataString(documentId);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            var encodedVersionId = Uri.EscapeDataString(versionId);
            candidates.Add($"/v1/Document/{encodedDocumentId}/{encodedVersionId}");
            candidates.Add($"/v1/Document/{encodedDocumentId}/{encodedVersionId}?download=true");
        }
        else
        {
            candidates.Add($"/v1/Document/{encodedDocumentId}");
            candidates.Add($"/v1/Document/{encodedDocumentId}?download=true");
        }

        NdBinaryDownloadResponse? lastFailure = null;
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var stream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                var response = await _apiClient.DownloadBinaryAsync(path, stream, cancellationToken);
                if (response.Succeeded)
                {
                    return response;
                }

                lastFailure = response;
                DeleteIfExists(destinationFilePath);
                if (response.StatusCode is 429 or 408 or 500 or 502 or 503 or 504)
                {
                    return response;
                }
            }
            catch (OperationCanceledException)
            {
                DeleteIfExists(destinationFilePath);
                throw;
            }
            catch (Exception ex)
            {
                DeleteIfExists(destinationFilePath);
                lastFailure = new NdBinaryDownloadResponse
                {
                    Succeeded = false,
                    StatusCode = 0,
                    RequestPath = path,
                    ErrorMessage = ex.Message
                };
            }
        }

        return lastFailure ?? new NdBinaryDownloadResponse
        {
            Succeeded = false,
            StatusCode = 0,
            ErrorMessage = "Document download failed for all endpoint variants."
        };
    }

    private async Task<NdExportPageResult> QueryContainerDocumentPageAsync(
        string cabinetId,
        string containerId,
        int top,
        int skip,
        string? skipToken,
        IReadOnlyList<string>? customAttributeIds,
        CancellationToken cancellationToken)
    {
        var selectTokens = BuildContainerDocumentSelectTokens(customAttributeIds);
        var select = Uri.EscapeDataString(string.Join(",", selectTokens));
        var listFlags = Uri.EscapeDataString("Documents,ByteSize");
        var normalizedContainerCandidates = BuildContainerIdCandidates(containerId, NormalizeWorkspaceEnvId(containerId))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedContainerCandidates.Count == 0)
        {
            normalizedContainerCandidates.Add(containerId);
        }

        foreach (var candidateContainerId in normalizedContainerCandidates)
        {
            var escapedCabinet = Uri.EscapeDataString(cabinetId);
            var escapedContainer = Uri.EscapeDataString(candidateContainerId);
            var encodedContainer = EncodeContainerIdForPath(candidateContainerId);
            var pagingSuffix = !string.IsNullOrWhiteSpace(skipToken)
                ? $"&skiptoken={Uri.EscapeDataString(skipToken)}"
                : $"&skip={skip}";
            var paths = new[]
            {
                $"/v2/search/{escapedCabinet}?container={escapedContainer}&top={top}{pagingSuffix}&select={select}&listflags={listFlags}",
                $"/v2/search/{escapedCabinet}?container={escapedContainer}&top={top}{pagingSuffix}&select={select}",
                $"/v2/container/{encodedContainer}?top={top}{pagingSuffix}&select={select}&listflags={listFlags}"
            };

            foreach (var path in paths)
            {
                try
                {
                    using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                    var parsed = ParseContainerDocumentRows(document.RootElement, candidateContainerId, customAttributeIds);
                    var nextOffset = ReadNextOffset(document.RootElement);
                    var nextSkipToken = ReadNextSkipToken(document.RootElement);
                    if (parsed.Count > 0)
                    {
                        return new NdExportPageResult(parsed, nextOffset, nextSkipToken);
                    }

                    if (skip > 0 || !string.IsNullOrWhiteSpace(skipToken))
                    {
                        return new NdExportPageResult(parsed, nextOffset, nextSkipToken);
                    }
                }
                catch
                {
                    // Try next endpoint variant.
                }
            }
        }

        return new NdExportPageResult(Array.Empty<NdExportDocument>(), null);
    }

    private static int? ReadNextOffset(JsonElement root)
    {
        return ReadNullableInt(root, "nextOffset", "NextOffset", "nextoffset", "offset");
    }

    private static string? ReadNextSkipToken(JsonElement root)
    {
        return ReadString(root, "skipToken", "SkipToken", "nextToken", "NextToken");
    }

    private static IReadOnlyList<string> BuildContainerDocumentSelectTokens(IReadOnlyList<string>? customAttributeIds)
    {
        var tokens = new List<string>
        {
            "StandardAttributes",
            "VersionsLite",
            "ByteSize"
        };

        if (customAttributeIds is not null)
        {
            foreach (var field in customAttributeIds)
            {
                if (string.IsNullOrWhiteSpace(field))
                {
                    continue;
                }

                if (!tokens.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    tokens.Add(field);
                }
            }
        }

        return tokens;
    }

    private static IReadOnlyList<NdExportDocument> ParseContainerDocumentRows(
        JsonElement root,
        string containerId,
        IReadOnlyList<string>? selectedCustomAttributeIds)
    {
        var rows = new List<NdExportDocument>();
        foreach (var item in EnumerateSearchItems(root))
        {
            var source = SelectTargetSelectionSource(item);
            var rawType = ReadString(source, "type", "containerType", "kind", "extension", "ext");
            if (string.IsNullOrWhiteSpace(rawType))
            {
                rawType = ReadString(item, "type", "containerType", "kind", "extension", "ext");
            }

            if (IsContainerLikeExportType(rawType))
            {
                continue;
            }

            if (!HasDocumentIdentifierHint(item, source, containerId) && !IsDocumentLikeType(rawType))
            {
                continue;
            }

            var parsed = ParseContainerDocumentRow(item, source, selectedCustomAttributeIds);
            if (parsed is null)
            {
                continue;
            }

            rows.Add(parsed);
        }

        return rows;
    }

    private static bool IsContainerLikeExportType(string? rawType)
    {
        var normalized = (rawType ?? string.Empty)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();

        return normalized is "ndfld" or "fld" or "folder" or "ndflt" or "workspacefilter" or "filter" or "ndsq" or "savedsearch" or "search" or "ndws" or "workspace" or "ndcs" or "collabspace";
    }

    private static NdExportDocument? ParseContainerDocumentRow(
        JsonElement item,
        JsonElement source,
        IReadOnlyList<string>? selectedCustomAttributeIds)
    {
        var documentId = ReadString(source, "docId", "documentId", "id", "envId");
        if (string.IsNullOrWhiteSpace(documentId))
        {
            documentId = ReadString(item, "docId", "documentId", "id", "envId");
        }

        if (string.IsNullOrWhiteSpace(documentId))
        {
            return null;
        }

        var fileName = ReadString(source, "name", "description", "title", "docName", "filename");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = ReadString(item, "name", "description", "title", "docName", "filename");
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = documentId;
        }

        var row = new NdExportDocument
        {
            DocumentId = documentId,
            FileName = fileName,
            SizeBytes = ReadNullableLong(source, "sizeBytes", "size", "bytes", "contentLength")
                        ?? ReadNullableLong(item, "sizeBytes", "size", "bytes", "contentLength"),
            OfficialVersionId = ReadOfficialVersionId(source) ?? ReadOfficialVersionId(item)
        };
        row.VersionHints.AddRange(ReadVersionsLite(source));
        row.VersionHints.AddRange(ReadVersionsLite(item));
        if (row.VersionHints.Count > 1)
        {
            var dedupedHints = row.VersionHints
                .Where(version => !string.IsNullOrWhiteSpace(version.VersionId))
                .GroupBy(version => version.VersionId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            row.VersionHints.Clear();
            row.VersionHints.AddRange(dedupedHints);
        }
        if (string.IsNullOrWhiteSpace(row.OfficialVersionId))
        {
            row.OfficialVersionId = row.VersionHints
                .FirstOrDefault(version => version.IsOfficial)?
                .VersionId;
        }

        row.StandardAttributes.AddRange(ReadStandardAttributes(source));
        row.StandardAttributes.AddRange(ReadStandardAttributes(item));
        row.CustomAttributes.AddRange(ReadCustomAttributes(source));
        row.CustomAttributes.AddRange(ReadCustomAttributes(item));
        row.CustomAttributes.AddRange(ReadSelectedCustomAttributes(source, selectedCustomAttributeIds));
        row.CustomAttributes.AddRange(ReadSelectedCustomAttributes(item, selectedCustomAttributeIds));

        DeduplicateAttributeValues(row.StandardAttributes);
        DeduplicateAttributeValues(row.CustomAttributes);
        return row;
    }

    private static IReadOnlyList<NdExportAttributeValue> ReadSelectedCustomAttributes(
        JsonElement node,
        IReadOnlyList<string>? selectedCustomAttributeIds)
    {
        var attributes = new List<NdExportAttributeValue>();
        if (selectedCustomAttributeIds is null || selectedCustomAttributeIds.Count == 0)
        {
            return attributes;
        }

        foreach (var attributeId in selectedCustomAttributeIds)
        {
            if (string.IsNullOrWhiteSpace(attributeId))
            {
                continue;
            }

            if (!TryGetPropertyIgnoreCase(node, attributeId, out var valueNode))
            {
                continue;
            }

            var value = ReadValueAsString(valueNode);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            attributes.Add(new NdExportAttributeValue
            {
                Name = $"custom.{attributeId}",
                Value = value
            });
        }

        return attributes;
    }

    private static IReadOnlyList<NdExportDocumentVersion> ParseDocumentVersions(JsonElement root)
    {
        var results = new List<NdExportDocumentVersion>();
        foreach (var item in EnumerateArray(root))
        {
            var versionId = ReadString(item, "versionId", "version", "id", "verNo", "ver");
            if (string.IsNullOrWhiteSpace(versionId))
            {
                continue;
            }

            var fileName = ReadString(item, "name", "description", "title", "docName", "filename");
            if (string.IsNullOrWhiteSpace(fileName) &&
                TryGetPropertyIgnoreCase(item, "standardAttributes", out var standardNode))
            {
                fileName = ReadString(standardNode, "name", "description", "title", "docName", "filename");
            }

            results.Add(new NdExportDocumentVersion
            {
                VersionId = versionId,
                FileName = fileName,
                SizeBytes = ReadNullableLong(item, "sizeBytes", "size", "bytes", "contentLength"),
                IsOfficial = ReadBool(item, "official", "isOfficial", "officialVersion", "isCurrent"),
                Attributes = ReadStandardAttributes(item).ToList()
            });
        }

        return results;
    }

    private static string? ReadOfficialVersionId(JsonElement node)
    {
        if (TryGetPropertyIgnoreCase(node, "versionsLite", out var versionsLiteNode))
        {
            if (versionsLiteNode.ValueKind == JsonValueKind.Object)
            {
                var value = ReadString(
                    versionsLiteNode,
                    "officialVersionId",
                    "officialVersion",
                    "currentVersionId",
                    "latestVersionId",
                    "versionId",
                    "id");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            else if (versionsLiteNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var versionNode in versionsLiteNode.EnumerateArray())
                {
                    var isOfficial = ReadBool(versionNode, "official", "isOfficial", "officialVersion", "isCurrent");
                    if (!isOfficial)
                    {
                        continue;
                    }

                    var officialVersionId = ReadString(
                        versionNode,
                        "versionId",
                        "version",
                        "id",
                        "verNo",
                        "ver");
                    if (!string.IsNullOrWhiteSpace(officialVersionId))
                    {
                        return officialVersionId;
                    }
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<NdExportDocumentVersion> ReadVersionsLite(JsonElement node)
    {
        var versions = new List<NdExportDocumentVersion>();
        if (!TryGetPropertyIgnoreCase(node, "versionsLite", out var versionsLiteNode))
        {
            return versions;
        }

        if (versionsLiteNode.ValueKind == JsonValueKind.Object)
        {
            var versionId = ReadString(
                versionsLiteNode,
                "officialVersionId",
                "officialVersion",
                "currentVersionId",
                "latestVersionId",
                "versionId",
                "id");
            if (!string.IsNullOrWhiteSpace(versionId))
            {
                versions.Add(new NdExportDocumentVersion
                {
                    VersionId = versionId,
                    IsOfficial = true
                });
            }

            return versions;
        }

        if (versionsLiteNode.ValueKind != JsonValueKind.Array)
        {
            return versions;
        }

        foreach (var versionNode in versionsLiteNode.EnumerateArray())
        {
            var versionId = ReadString(
                versionNode,
                "versionId",
                "version",
                "id",
                "verNo",
                "ver");
            if (string.IsNullOrWhiteSpace(versionId))
            {
                continue;
            }

            versions.Add(new NdExportDocumentVersion
            {
                VersionId = versionId,
                FileName = ReadString(versionNode, "name", "description", "title", "docName", "filename"),
                SizeBytes = ReadNullableLong(versionNode, "sizeBytes", "size", "bytes", "contentLength"),
                IsOfficial = ReadBool(versionNode, "official", "isOfficial", "officialVersion", "isCurrent")
            });
        }

        return versions;
    }

    private static IReadOnlyList<NdExportAttributeValue> ReadStandardAttributes(JsonElement node)
    {
        var attributes = new List<NdExportAttributeValue>();
        if (!TryGetPropertyIgnoreCase(node, "standardAttributes", out var standardNode))
        {
            return attributes;
        }

        if (standardNode.ValueKind != JsonValueKind.Object)
        {
            return attributes;
        }

        foreach (var property in standardNode.EnumerateObject())
        {
            var value = ReadValueAsString(property.Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            attributes.Add(new NdExportAttributeValue
            {
                Name = $"standard.{property.Name}",
                Value = value
            });
        }

        return attributes;
    }

    private static IReadOnlyList<NdExportAttributeValue> ReadCustomAttributes(JsonElement node)
    {
        var attributes = new List<NdExportAttributeValue>();

        foreach (var propertyName in new[] { "customAttributes", "attributes" })
        {
            if (!TryGetPropertyIgnoreCase(node, propertyName, out var customNode))
            {
                continue;
            }

            if (customNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in customNode.EnumerateObject())
                {
                    var value = ReadValueAsString(property.Value);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    attributes.Add(new NdExportAttributeValue
                    {
                        Name = $"custom.{property.Name}",
                        Value = value
                    });
                }

                continue;
            }

            if (customNode.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in customNode.EnumerateArray())
            {
                var key = ReadString(item, "attributeId", "attrId", "id", "name", "field");
                var value = ReadString(item, "rawValue", "value", "description", "label", "name");
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                attributes.Add(new NdExportAttributeValue
                {
                    Name = $"custom.{key}",
                    Value = value
                });
            }
        }

        return attributes;
    }

    private static void DeduplicateAttributeValues(IList<NdExportAttributeValue> values)
    {
        if (values.Count <= 1)
        {
            return;
        }

        var dedupe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in values)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            dedupe[entry.Name] = entry.Value;
        }

        values.Clear();
        foreach (var pair in dedupe)
        {
            values.Add(new NdExportAttributeValue
            {
                Name = pair.Key,
                Value = pair.Value
            });
        }
    }

    private static NdExportScope BuildScopeFromSelection(
        NdTargetSelection selection,
        string? parentContainerId,
        IEnumerable<string> pathSegments)
    {
        return new NdExportScope
        {
            ContainerId = selection.Id,
            Name = string.IsNullOrWhiteSpace(selection.Name) ? selection.Id : selection.Name,
            TargetType = selection.Type,
            Extension = selection.Extension ?? string.Empty,
            Kind = ResolveScopeKind(selection),
            ParentContainerId = parentContainerId,
            PathSegments = pathSegments
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToList()
        };
    }

    private static NdExportScopeKind ResolveScopeKind(NdTargetSelection selection)
    {
        return selection.Type switch
        {
            NdTargetType.Workspace => NdExportScopeKind.Workspace,
            NdTargetType.WorkspaceFilter when NdTargetBrowserLogic.IsSavedSearchTarget(selection.Id, selection.Extension) => NdExportScopeKind.SavedSearch,
            NdTargetType.WorkspaceFilter => NdExportScopeKind.WorkspaceFilter,
            NdTargetType.Folder when NdTargetBrowserLogic.IsCollabspaceIdentifier(selection.Id) => NdExportScopeKind.Collabspace,
            _ => NdExportScopeKind.Folder
        };
    }

    private static string BuildScopeDedupeKey(NdTargetType type, string containerId)
    {
        var normalizedId = NormalizeContainerIdentityForDedupe(containerId);
        return $"{type}:{normalizedId}";
    }

    private static long? ReadNullableLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numberValue))
            {
                return numberValue;
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numberValue))
            {
                return numberValue;
            }
        }

        return null;
    }

    private static void DeleteIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private sealed record NdExportPageResult(
        IReadOnlyList<NdExportDocument> Items,
        int? NextOffset,
        string? NextSkipToken = null);
}
