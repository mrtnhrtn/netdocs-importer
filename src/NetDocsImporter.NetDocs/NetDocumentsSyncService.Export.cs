using System.Globalization;
using System.Text.Json;
using NetDocsImporter.Core;

namespace NetDocsImporter.NetDocs;

public sealed partial class NetDocumentsSyncService
{
    public async Task<IReadOnlyList<NdExportScope>> EnumerateExportScopesAsync(
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
        var queue = new Queue<NdExportScope>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rootScope = BuildScopeFromSelection(root, parentContainerId: null, pathSegments: new[]
        {
            string.IsNullOrWhiteSpace(root.Name) ? root.Id : root.Name
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
                    cancellationToken: cancellationToken);
            }
            catch
            {
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

        return result;
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
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await QueryContainerDocumentPageAsync(cabinetId, scope.ContainerId, pageSize, skip, customAttributeIds, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var row in page)
            {
                if (!documents.ContainsKey(row.DocumentId))
                {
                    documents[row.DocumentId] = row;
                }
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
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return Array.Empty<NdExportDocumentVersion>();
        }

        var encodedDocumentId = Uri.EscapeDataString(documentId);
        var select = Uri.EscapeDataString("StandardAttributes,Descriptions,DispNames,CustomAttributes,StatusAttributes,UseLongName,ByteSize");
        var candidates = new[]
        {
            $"/v2/document/{encodedDocumentId}/version?select={select}",
            $"/v2/document/{encodedDocumentId}/versions?select={select}",
            $"/v1/Document/{encodedDocumentId}/version"
        };

        foreach (var path in candidates)
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                var versions = ParseDocumentVersions(document.RootElement);
                if (versions.Count > 0)
                {
                    return versions;
                }
            }
            catch
            {
                // Try next endpoint variant.
            }
        }

        return Array.Empty<NdExportDocumentVersion>();
    }

    private async Task<IReadOnlyList<NdExportDocument>> QueryContainerDocumentPageAsync(
        string cabinetId,
        string containerId,
        int top,
        int skip,
        IReadOnlyList<string>? customAttributeIds,
        CancellationToken cancellationToken)
    {
        var escapedCabinet = Uri.EscapeDataString(cabinetId);
        var select = BuildContainerDocumentSelect(customAttributeIds);
        var listFlags = Uri.EscapeDataString("Documents,ByteSize,ValidateWorkspaces");
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
            var escapedContainer = Uri.EscapeDataString(candidateContainerId);
            var encodedContainer = EncodeContainerIdForPath(candidateContainerId);
            var paths = new[]
            {
                $"/v2/search/{escapedCabinet}?container={escapedContainer}&top={top}&skip={skip}&select={select}&listflags={listFlags}",
                $"/v2/search/{escapedCabinet}?container={escapedContainer}&top={top}&skip={skip}&select={select}",
                $"/v2/container/{encodedContainer}/search?top={top}&skip={skip}&select={select}&listflags={listFlags}",
                $"/v2/container/{encodedContainer}?top={top}&skip={skip}&select={select}&listflags={listFlags}"
            };

            foreach (var path in paths)
            {
                try
                {
                    using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                    var parsed = ParseContainerDocumentRows(document.RootElement, candidateContainerId);
                    if (parsed.Count > 0)
                    {
                        return parsed;
                    }

                    if (skip > 0)
                    {
                        return parsed;
                    }
                }
                catch
                {
                    // Try next endpoint variant.
                }
            }
        }

        return Array.Empty<NdExportDocument>();
    }

    private static string BuildContainerDocumentSelect(IReadOnlyList<string>? customAttributeIds)
    {
        var tokens = new List<string>
        {
            "StandardAttributes",
            "Descriptions",
            "DispNames",
            "VersionsLite",
            "StatusAttributes",
            "UseLongName",
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

        return Uri.EscapeDataString(string.Join(",", tokens));
    }

    private static IReadOnlyList<NdExportDocument> ParseContainerDocumentRows(JsonElement root, string containerId)
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

            if (!HasDocumentIdentifierHint(item, source, containerId) && !IsDocumentLikeType(rawType))
            {
                continue;
            }

            var parsed = ParseContainerDocumentRow(item, source);
            if (parsed is null)
            {
                continue;
            }

            rows.Add(parsed);
        }

        return rows;
    }

    private static NdExportDocument? ParseContainerDocumentRow(JsonElement item, JsonElement source)
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

        row.StandardAttributes.AddRange(ReadStandardAttributes(source));
        row.StandardAttributes.AddRange(ReadStandardAttributes(item));
        row.CustomAttributes.AddRange(ReadCustomAttributes(source));
        row.CustomAttributes.AddRange(ReadCustomAttributes(item));

        DeduplicateAttributeValues(row.StandardAttributes);
        DeduplicateAttributeValues(row.CustomAttributes);
        return row;
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
}
