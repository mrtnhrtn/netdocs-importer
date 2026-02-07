using System.Diagnostics;
using System.Text.Json;
using NetDocsImporter.Core;

namespace NetDocsImporter.NetDocs;

public sealed partial class NetDocumentsSyncService
{
    public async Task<IReadOnlyList<NdTargetSelection>> GetSupportedTargetContainersAsync(
        string cabinetId,
        CancellationToken cancellationToken = default)
    {
        var targets = new List<NdTargetSelection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var extension in new[] { "ndws", "ndflt", "ndfld" })
        {
            try
            {
                var matches = await SearchTargetSelectionsByExtensionAsync(cabinetId, extension, null, cancellationToken);
                foreach (var parsed in matches)
                {
                    if (seen.Add(parsed.Id))
                    {
                        targets.Add(parsed);
                    }
                }
            }
            catch
            {
                // Keep trying fallback endpoint variants.
            }
        }

        foreach (var path in BuildContainerEndpointCandidates(cabinetId))
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                foreach (var item in EnumerateArray(document.RootElement))
                {
                    var parsed = ParseTargetSelection(item);
                    if (parsed is null)
                    {
                        continue;
                    }

                    if (seen.Add(parsed.Id))
                    {
                        targets.Add(parsed);
                    }
                }
            }
            catch
            {
                // Keep trying known endpoint variants.
            }
        }

        return targets
            .OrderBy(t => t.Type)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<NdTargetRecentItem>> GetRecentTargetsAsync(
        string cabinetId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<NdTargetRecentItem>();
        foreach (var path in BuildRecentEndpointCandidatesV2(cabinetId))
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                results.AddRange(EnumerateArray(document.RootElement)
                    .Select(ParseRecentItem)
                    .Where(item => item is not null)
                    .Select(item => item!));
                if (results.Count > 0)
                {
                    Trace.WriteLine($"NetDocuments target browser: recent targets loaded from server ({results.Count}).");
                    return results;
                }
            }
            catch
            {
                // Continue to v1 fallback candidates.
            }
        }

        foreach (var path in BuildRecentEndpointCandidates(cabinetId))
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                results.AddRange(EnumerateArray(document.RootElement)
                    .Select(ParseRecentItem)
                    .Where(item => item is not null)
                    .Select(item => item!));
                if (results.Count > 0)
                {
                    Trace.WriteLine($"NetDocuments target browser: recent targets loaded from server ({results.Count}).");
                    break;
                }
            }
            catch
            {
                // Continue to fallback candidates.
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<NdTargetFavoriteItem>> GetFavoriteTargetsAsync(
        string cabinetId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<NdTargetFavoriteItem>();
        foreach (var path in BuildFavoriteEndpointCandidatesV2(cabinetId))
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                results.AddRange(EnumerateArray(document.RootElement)
                    .Select(ParseFavoriteItem)
                    .Where(item => item is not null)
                    .Select(item => item!));
                if (results.Count > 0)
                {
                    Trace.WriteLine($"NetDocuments target browser: favorites loaded from server ({results.Count}).");
                    return results;
                }
            }
            catch
            {
                // Continue to v1 fallback candidates.
            }
        }

        foreach (var path in BuildFavoriteEndpointCandidates(cabinetId))
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                results.AddRange(EnumerateArray(document.RootElement)
                    .Select(ParseFavoriteItem)
                    .Where(item => item is not null)
                    .Select(item => item!));
                if (results.Count > 0)
                {
                    Trace.WriteLine($"NetDocuments target browser: favorites loaded from server ({results.Count}).");
                    break;
                }
            }
            catch
            {
                // Continue to fallback candidates.
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<NdWorkspaceSearchResult>> SearchWorkspacesAsync(
        string cabinetId,
        string query,
        int top = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<NdWorkspaceSearchResult>();
        }

        var escapedCabinetId = Uri.EscapeDataString(cabinetId);
        var escapedQuery = Uri.EscapeDataString(query.Trim());
        var results = new List<NdWorkspaceSearchResult>();

        try
        {
            var items = await SearchTargetSelectionsByExtensionAsync(
                cabinetId,
                "ndws",
                query.Trim(),
                cancellationToken,
                Math.Max(10, top));
            results.AddRange(items.Select(item => new NdWorkspaceSearchResult
            {
                WorkspaceId = item.Id,
                WorkspaceName = item.Name,
                RepositoryId = string.Empty,
                CabinetId = cabinetId
            }));
            if (results.Count > 0)
            {
                return results
                    .OrderBy(item => item.WorkspaceName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch
        {
            // Continue to v1 fallback candidates.
        }

        foreach (var path in new[]
                 {
                     $"/v1/Cabinet/{escapedCabinetId}/workspaces?$search={escapedQuery}&$top={top}",
                     $"/v1/Cabinet/{escapedCabinetId}/containers?$search={escapedQuery}&type=workspace&$top={top}",
                     $"/v1/workspaces?$search={escapedQuery}&$top={top}"
                 })
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                var items = EnumerateArray(document.RootElement)
                    .Select(element => new NdWorkspaceSearchResult
                    {
                        WorkspaceId = ReadString(element, "id", "workspaceId"),
                        WorkspaceName = ReadString(element, "name", "description", "label"),
                        RepositoryId = ReadString(element, "repositoryId", "repoId"),
                        CabinetId = ReadString(element, "cabinetId")
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.WorkspaceId))
                    .ToList();
                if (items.Count > 0)
                {
                    results.AddRange(items);
                    break;
                }
            }
            catch
            {
                // Continue to endpoint fallback.
            }
        }

        return results
            .OrderBy(item => item.WorkspaceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<NdContainerNode>> GetContainerChildrenAsync(
        string cabinetId,
        string? parentContainerId = null,
        string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<NdContainerNode>();
        var searchScopeId = !string.IsNullOrWhiteSpace(parentContainerId)
            ? parentContainerId
            : workspaceId;
        if (!string.IsNullOrWhiteSpace(searchScopeId))
        {
            try
            {
                foreach (var extension in new[] { "ndws", "ndflt", "ndfld" })
                {
                    var items = await SearchTargetSelectionsByExtensionAsync(
                        cabinetId,
                        extension,
                        null,
                        cancellationToken,
                        top: 200,
                        containerId: searchScopeId);
                    foreach (var item in items)
                    {
                        results.Add(new NdContainerNode
                        {
                            Id = item.Id,
                            Name = item.Name,
                            TypeRaw = item.Type.ToString(),
                            ParentId = parentContainerId ?? string.Empty,
                            ParentWorkspaceId = item.ParentWorkspaceId,
                            PathDisplay = string.Empty,
                            SupportedType = item.Type,
                            IsSelectable = true,
                            UnsupportedReason = string.Empty,
                            HasChildren = item.Type != NdTargetType.WorkspaceFilter,
                            ChildrenLoadState = NdChildrenLoadState.NotLoaded
                        });
                    }
                }

                if (results.Count > 0)
                {
                    return results
                        .OrderByDescending(node => node.IsSelectable)
                        .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
            catch
            {
                // Continue to endpoint fallback.
            }
        }

        foreach (var path in BuildChildrenEndpointCandidates(cabinetId, parentContainerId, workspaceId))
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                var nodes = EnumerateArray(document.RootElement)
                    .Select(ParseContainerNode)
                    .Where(node => node is not null)
                    .Select(node => node!)
                    .ToList();
                if (nodes.Count > 0)
                {
                    results.AddRange(nodes);
                    break;
                }
            }
            catch
            {
                // Continue to endpoint fallback.
            }
        }

        return results
            .OrderByDescending(node => node.IsSelectable)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> ResolveTargetPathAsync(
        string cabinetId,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return string.Empty;
        }

        var escapedCabinet = Uri.EscapeDataString(cabinetId);
        var escapedTarget = Uri.EscapeDataString(targetId);
        foreach (var path in new[]
                 {
                     $"/v2/container/{escapedTarget}/ancestry",
                     $"/v2/container/{escapedTarget}/info",
                     $"/v1/Cabinet/{escapedCabinet}/containers/{escapedTarget}",
                     $"/v1/Container/{escapedTarget}/info"
                 })
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                var root = document.RootElement;
                var fullPath = ReadString(root, "path", "fullPath", "breadcrumb");
                if (!string.IsNullOrWhiteSpace(fullPath))
                {
                    return fullPath;
                }

                var ancestry = EnumerateArray(root)
                    .Select(item => ReadString(item, "name", "description"))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
                if (ancestry.Count > 0)
                {
                    return string.Join(" / ", ancestry);
                }

                var name = ReadString(root, "name", "description");
                return name;
            }
            catch
            {
                // Continue fallback
            }
        }

        return targetId;
    }

    public async Task<NdTargetProfileSnapshot> GetTargetProfileSnapshotAsync(
        string cabinetId,
        string repositoryId,
        NdTargetSelection target,
        CancellationToken cancellationToken = default)
    {
        await _jobStore.InitializeAsync(cancellationToken);

        var attributes = await TryFetchTargetProfileAttributesAsync(target, cancellationToken);
        if (attributes.Count == 0)
        {
            var synced = await _jobStore.GetNetDocumentsAttributesAsync(cabinetId, cancellationToken);
            attributes = synced
                .Where(a => string.Equals(a.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
                .Select(a => new NdProfileAttribute
                {
                    AttributeId = string.IsNullOrWhiteSpace(a.AttributeId) ? a.AttributeNum.ToString() : a.AttributeId,
                    AttributeNum = a.AttributeNum,
                    Name = a.Name,
                    IsRequired = a.IsRequired,
                    DataType = a.DataType,
                    IsPicklist = a.IsLookup
                })
                .ToList();
        }

        var defaults = await TryFetchTargetDefaultsAsync(target, attributes, cancellationToken);
        await ResolveDefaultDisplayValuesAsync(cabinetId, attributes, defaults, cancellationToken);

        Trace.WriteLine(
            $"NetDocuments target profile sync: target={target.Type}:{target.Id}, attributes={attributes.Count}, defaults={defaults.ValuesByAttributeId.Count}");

        return new NdTargetProfileSnapshot
        {
            Target = target,
            Attributes = attributes,
            EffectiveDefaults = defaults,
            SyncedUtc = DateTime.UtcNow
        };
    }

    private static IEnumerable<string> BuildContainerEndpointCandidates(string cabinetId)
    {
        var escaped = Uri.EscapeDataString(cabinetId);
        yield return $"/v1/Cabinet/{escaped}/containers";
        yield return $"/v1/Cabinet/{escaped}/containers?$select=id,name,type,parentId,parentWorkspaceId,parentName";
        yield return $"/v1/Cabinet/{escaped}/workspaces";
        yield return $"/v1/Cabinet/{escaped}/folders";
    }

    private static NdTargetSelection? ParseTargetSelection(JsonElement element)
    {
        var id = ReadString(element, "id", "containerId", "workspaceId", "folderId", "docId", "envelopeId", "documentId");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var rawType = ReadString(element, "type", "containerType", "kind", "extension", "ext");
        var resolvedType = NdTargetBrowserLogic.NormalizeSupportedType(rawType, element.TryGetProperty("workspaceId", out _));
        if (resolvedType is null)
        {
            return null;
        }

        return new NdTargetSelection
        {
            Id = id,
            Name = ReadString(element, "name", "description", "label", "title"),
            Type = resolvedType.Value,
            ParentWorkspaceId = ReadString(element, "parentWorkspaceId", "workspaceId", "parentId", "workspace")
        };
    }

    private async Task<List<NdProfileAttribute>> TryFetchTargetProfileAttributesAsync(
        NdTargetSelection target,
        CancellationToken cancellationToken)
    {
        foreach (var path in BuildProfileAttributeEndpointCandidates(target))
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                var parsed = EnumerateArray(document.RootElement)
                    .Select(ParseNdProfileAttribute)
                    .Where(a => a is not null)
                    .Select(a => a!)
                    .ToList();
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
            catch
            {
                // Try next endpoint.
            }
        }

        return new List<NdProfileAttribute>();
    }

    private static IEnumerable<string> BuildProfileAttributeEndpointCandidates(NdTargetSelection target)
    {
        var escaped = Uri.EscapeDataString(target.Id);
        yield return $"/v1/Container/{escaped}/profileAttributes";
        yield return $"/v1/Containers/{escaped}/profileAttributes";
        yield return $"/v1/Container/{escaped}/attributes";
        if (target.Type == NdTargetType.Workspace)
        {
            yield return $"/v1/Workspace/{escaped}/profileAttributes";
        }
    }

    private static NdProfileAttribute? ParseNdProfileAttribute(JsonElement item)
    {
        var id = ReadString(item, "attributeId", "id", "attrId", "attrNum");
        var name = ReadString(item, "name", "label", "description");
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var attrNum = ReadNullableInt(item, "attrNum", "attributeNum", "number");
        if (string.IsNullOrWhiteSpace(id) && attrNum.HasValue)
        {
            id = attrNum.Value.ToString();
        }

        return new NdProfileAttribute
        {
            AttributeId = id,
            AttributeNum = attrNum,
            Name = name,
            IsRequired = ReadBool(item, "isRequired", "required"),
            DataType = ReadString(item, "dataType", "type"),
            IsPicklist = ReadBool(item, "isPicklist", "isLookup", "lookup")
        };
    }

    private static IEnumerable<string> BuildRecentEndpointCandidates(string cabinetId)
    {
        var escaped = Uri.EscapeDataString(cabinetId);
        yield return $"/v1/User/recentLocations?cabinetId={escaped}";
        yield return $"/v1/User/recent?cabinetId={escaped}";
        yield return $"/v1/Cabinet/{escaped}/recentLocations";
    }

    private static IEnumerable<string> BuildRecentEndpointCandidatesV2(string cabinetId)
    {
        var escaped = Uri.EscapeDataString(cabinetId);
        yield return $"/v2/user/recent/locations/{escaped}";
        yield return $"/v2/user/recent/workspaces/{escaped}";
        yield return "/v2/user/recent/locations";
    }

    private static IEnumerable<string> BuildFavoriteEndpointCandidates(string cabinetId)
    {
        var escaped = Uri.EscapeDataString(cabinetId);
        yield return $"/v1/User/favoriteLocations?cabinetId={escaped}";
        yield return $"/v1/User/favorites?cabinetId={escaped}";
        yield return $"/v1/Cabinet/{escaped}/favoriteLocations";
    }

    private static IEnumerable<string> BuildFavoriteEndpointCandidatesV2(string cabinetId)
    {
        var escaped = Uri.EscapeDataString(cabinetId);
        yield return $"/v2/user/favorites/{escaped}";
        yield return "/v2/user/favorites";
    }

    private static IEnumerable<string> BuildChildrenEndpointCandidates(string cabinetId, string? parentContainerId, string? workspaceId)
    {
        var escapedCabinetId = Uri.EscapeDataString(cabinetId);
        if (!string.IsNullOrWhiteSpace(parentContainerId))
        {
            var escapedParent = Uri.EscapeDataString(parentContainerId);
            yield return $"/v1/Container/{escapedParent}/children";
            yield return $"/v1/Containers/{escapedParent}/children";
            yield return $"/v1/Cabinet/{escapedCabinetId}/containers/{escapedParent}/children";
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            var escapedWorkspaceId = Uri.EscapeDataString(workspaceId);
            yield return $"/v1/Workspace/{escapedWorkspaceId}/children";
            yield return $"/v1/Cabinet/{escapedCabinetId}/workspaces/{escapedWorkspaceId}/children";
            yield break;
        }

        yield return $"/v1/Cabinet/{escapedCabinetId}/workspaces";
        yield return $"/v1/Cabinet/{escapedCabinetId}/containers";
    }

    private static NdTargetRecentItem? ParseRecentItem(JsonElement element)
    {
        var selection = ParseTargetSelection(element);
        if (selection is null)
        {
            return null;
        }

        var timestampRaw = ReadString(element, "lastUsedUtc", "lastAccessedUtc", "timestamp");
        var timestamp = DateTime.TryParse(timestampRaw, out var parsedUtc)
            ? parsedUtc.ToUniversalTime()
            : DateTime.UtcNow;

        return new NdTargetRecentItem
        {
            Selection = selection,
            LastUsedUtc = timestamp,
            Source = NdTargetSource.Server
        };
    }

    private static NdTargetFavoriteItem? ParseFavoriteItem(JsonElement element)
    {
        var selection = ParseTargetSelection(element);
        if (selection is null)
        {
            return null;
        }

        var timestampRaw = ReadString(element, "pinnedUtc", "createdUtc", "timestamp");
        var timestamp = DateTime.TryParse(timestampRaw, out var parsedUtc)
            ? parsedUtc.ToUniversalTime()
            : DateTime.UtcNow;

        return new NdTargetFavoriteItem
        {
            Selection = selection,
            PinnedUtc = timestamp,
            Source = NdTargetSource.Server
        };
    }

    private static NdContainerNode? ParseContainerNode(JsonElement element)
    {
        var id = ReadString(element, "id", "containerId", "workspaceId", "folderId", "docId", "envelopeId", "documentId");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var rawType = ReadString(element, "type", "containerType", "kind", "extension", "ext");
        var supportedType = NdTargetBrowserLogic.NormalizeSupportedType(rawType, element.TryGetProperty("workspaceId", out _));
        var name = ReadString(element, "name", "description", "label", "title");

        return new NdContainerNode
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            TypeRaw = rawType,
            ParentId = ReadString(element, "parentId"),
            ParentWorkspaceId = ReadString(element, "parentWorkspaceId", "workspaceId", "workspace"),
            PathDisplay = ReadString(element, "path", "fullPath", "breadcrumb"),
            SupportedType = supportedType,
            IsSelectable = supportedType.HasValue,
            UnsupportedReason = NdTargetBrowserLogic.GetUnsupportedReason(supportedType),
            HasChildren = ReadBool(element, "hasChildren", "containsChildren", "canExpand"),
            ChildrenLoadState = NdChildrenLoadState.NotLoaded
        };
    }

    private async Task<IReadOnlyList<NdTargetSelection>> SearchTargetSelectionsByExtensionAsync(
        string cabinetId,
        string extension,
        string? query,
        CancellationToken cancellationToken,
        int top = 200,
        string? containerId = null)
    {
        var escapedCabinet = Uri.EscapeDataString(cabinetId);
        var escapedQuery = Uri.EscapeDataString(query ?? string.Empty);
        var escapedContainer = Uri.EscapeDataString(containerId ?? string.Empty);
        var filter = Uri.EscapeDataString($"extension eq {extension}");

        var candidates = new List<string>
        {
            $"/v2/search/{escapedCabinet}?top={top}&filter={filter}&filtertype=IncludeOnly&listflags=FoldersOnly,ValidateWorkspaces"
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            candidates.Add($"/v2/search/{escapedCabinet}?q={escapedQuery}&top={top}&filter={filter}&filtertype=IncludeOnly&listflags=FoldersOnly,ValidateWorkspaces");
            candidates.Add($"/v2/search?cabinets={escapedCabinet}&q={escapedQuery}&top={top}&filter={filter}&filtertype=IncludeOnly&listflags=FoldersOnly,ValidateWorkspaces");
        }
        else
        {
            candidates.Add($"/v2/search?cabinets={escapedCabinet}&top={top}&filter={filter}&filtertype=IncludeOnly&listflags=FoldersOnly,ValidateWorkspaces");
        }

        if (!string.IsNullOrWhiteSpace(containerId))
        {
            candidates.Add($"/v2/search/{escapedCabinet}?container={escapedContainer}&top={top}&filter={filter}&filtertype=IncludeOnly&listflags=FoldersOnly,ValidateWorkspaces");
            candidates.Add($"/v2/search?container={escapedContainer}&top={top}&filter={filter}&filtertype=IncludeOnly&listflags=FoldersOnly,ValidateWorkspaces");
        }

        var results = new List<NdTargetSelection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidates)
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                foreach (var item in EnumerateSearchItems(document.RootElement))
                {
                    var parsed = ParseTargetSelection(item);
                    if (parsed is null)
                    {
                        continue;
                    }

                    if (!IsExtensionMatch(item, extension))
                    {
                        continue;
                    }

                    if (seen.Add(parsed.Id))
                    {
                        results.Add(parsed);
                    }
                }

                if (results.Count > 0)
                {
                    break;
                }
            }
            catch
            {
                // Try next candidate.
            }
        }

        return results;
    }

    private static IEnumerable<JsonElement> EnumerateSearchItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "items", "results", "data", "documents", "records", "value", "list" })
            {
                if (root.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Array)
                {
                    return child.EnumerateArray();
                }
            }
        }

        return Array.Empty<JsonElement>();
    }

    private static bool IsExtensionMatch(JsonElement element, string extension)
    {
        var ext = ReadString(element, "extension", "ext", "type", "containerType", "kind");
        if (string.IsNullOrWhiteSpace(ext))
        {
            return true;
        }

        return string.Equals(ext.Trim(), extension, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<EffectiveProfileDefaults> TryFetchTargetDefaultsAsync(
        NdTargetSelection target,
        IReadOnlyList<NdProfileAttribute> attributes,
        CancellationToken cancellationToken)
    {
        foreach (var path in BuildDefaultEndpointCandidates(target))
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                var parsed = ParseEffectiveDefaults(document.RootElement, attributes);
                if (parsed.HasValues)
                {
                    return parsed;
                }
            }
            catch
            {
                // Try next endpoint.
            }
        }

        return EffectiveProfileDefaults.Empty;
    }

    private static IEnumerable<string> BuildDefaultEndpointCandidates(NdTargetSelection target)
    {
        var escaped = Uri.EscapeDataString(target.Id);
        yield return $"/v1/Container/{escaped}/profileDefaults";
        yield return $"/v1/Containers/{escaped}/profileDefaults";
        yield return $"/v1/Container/{escaped}/inheritedProfileValues";
        if (target.Type == NdTargetType.Workspace)
        {
            yield return $"/v1/Workspace/{escaped}/profileDefaults";
        }
    }

    private static EffectiveProfileDefaults ParseEffectiveDefaults(JsonElement root, IReadOnlyList<NdProfileAttribute> attributes)
    {
        var defaults = new EffectiveProfileDefaults();
        var attrsById = attributes.ToDictionary(a => a.AttributeId, StringComparer.OrdinalIgnoreCase);
        var attrsByName = attributes.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("values", out var valuesNode))
            {
                AddDefaultsFromNode(valuesNode, defaults, attrsById, attrsByName);
            }
            else if (root.TryGetProperty("data", out var dataNode))
            {
                AddDefaultsFromNode(dataNode, defaults, attrsById, attrsByName);
            }
            else
            {
                AddDefaultsFromNode(root, defaults, attrsById, attrsByName);
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            AddDefaultsFromNode(root, defaults, attrsById, attrsByName);
        }

        return defaults;
    }

    private static void AddDefaultsFromNode(
        JsonElement node,
        EffectiveProfileDefaults defaults,
        IReadOnlyDictionary<string, NdProfileAttribute> attrsById,
        IReadOnlyDictionary<string, NdProfileAttribute> attrsByName)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                var attrId = ReadString(item, "attributeId", "attrId", "id", "fieldId");
                var attrName = ReadString(item, "attributeName", "field", "name", "label");
                var rawValue = ReadString(item, "value", "rawValue", "key", "id");
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    continue;
                }

                var attr = ResolveAttribute(attrsById, attrsByName, attrId, attrName);
                var resolvedId = attr?.AttributeId ?? attrId ?? attrName;
                if (string.IsNullOrWhiteSpace(resolvedId))
                {
                    continue;
                }

                defaults.ValuesByAttributeId[resolvedId] = new NdProfileValue
                {
                    AttributeId = resolvedId,
                    AttributeName = attr?.Name ?? attrName ?? resolvedId,
                    RawValue = rawValue,
                    DisplayValue = ReadString(item, "displayValue", "description", "label", "name"),
                    PicklistItemId = ReadString(item, "picklistItemId", "picklistId")
                };
            }

            return;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in node.EnumerateObject())
        {
            var attr = ResolveAttribute(attrsById, attrsByName, property.Name, property.Name);
            var attrId = attr?.AttributeId ?? property.Name;
            var rawValue = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            defaults.ValuesByAttributeId[attrId] = new NdProfileValue
            {
                AttributeId = attrId,
                AttributeName = attr?.Name ?? property.Name,
                RawValue = rawValue,
                DisplayValue = string.Empty
            };
        }
    }

    private static NdProfileAttribute? ResolveAttribute(
        IReadOnlyDictionary<string, NdProfileAttribute> attrsById,
        IReadOnlyDictionary<string, NdProfileAttribute> attrsByName,
        string? attrId,
        string? attrName)
    {
        if (!string.IsNullOrWhiteSpace(attrId) && attrsById.TryGetValue(attrId, out var byId))
        {
            return byId;
        }

        if (!string.IsNullOrWhiteSpace(attrName) && attrsByName.TryGetValue(attrName, out var byName))
        {
            return byName;
        }

        return null;
    }

    private async Task ResolveDefaultDisplayValuesAsync(
        string cabinetId,
        IReadOnlyList<NdProfileAttribute> attributes,
        EffectiveProfileDefaults defaults,
        CancellationToken cancellationToken)
    {
        if (!defaults.HasValues)
        {
            return;
        }

        var attrsById = attributes.ToDictionary(a => a.AttributeId, StringComparer.OrdinalIgnoreCase);
        foreach (var item in defaults.ValuesByAttributeId.Values)
        {
            if (!attrsById.TryGetValue(item.AttributeId, out var attribute) ||
                !attribute.IsPicklist ||
                !attribute.AttributeNum.HasValue)
            {
                item.DisplayValue = string.IsNullOrWhiteSpace(item.DisplayValue) ? item.RawValue : item.DisplayValue;
                continue;
            }

            var lookupValues = await _jobStore.GetNetDocumentsLookupValuesAsync(
                cabinetId,
                attribute.AttributeNum.Value,
                cancellationToken: cancellationToken);
            var match = lookupValues.FirstOrDefault(v => string.Equals(v.ValueKey, item.RawValue, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                Trace.WriteLine(
                    $"NetDocuments unresolved picklist value: attr={attribute.Name}, raw={item.RawValue}, targetAttributeId={item.AttributeId}");
                item.DisplayValue = string.IsNullOrWhiteSpace(item.DisplayValue) ? item.RawValue : item.DisplayValue;
                continue;
            }

            item.DisplayValue = string.IsNullOrWhiteSpace(match.Description) ? match.ValueKey : match.Description;
            item.PicklistItemId = match.ValueKey;
        }
    }
}
