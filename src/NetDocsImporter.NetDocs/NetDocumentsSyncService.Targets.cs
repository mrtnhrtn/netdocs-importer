using System.Diagnostics;
using System.Net.Http;
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
        string? cabinetId = null,
        bool bypassCache = false,
        CancellationToken cancellationToken = default)
    {
        return await GetWorkspaceListAsync("/v1/User/wsRecent", cabinetId, bypassCache, parseFavoriteShape: false, cancellationToken);
    }

    public async Task<IReadOnlyList<NdTargetFavoriteItem>> GetFavoriteTargetsAsync(
        string? cabinetId = null,
        bool bypassCache = false,
        CancellationToken cancellationToken = default)
    {
        var recentShape = await GetWorkspaceListAsync("/v1/User/wsFav", cabinetId, bypassCache, parseFavoriteShape: true, cancellationToken);
        return recentShape
            .Select(item => new NdTargetFavoriteItem
            {
                Selection = item.Selection,
                PinnedUtc = item.LastUsedUtc,
                Source = item.Source
            })
            .ToList();
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
            Trace.WriteLine($"NetDocuments workspace search: v2 extension search returned {items.Count} item(s) for query='{query}'.");
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
            Trace.WriteLine($"NetDocuments workspace search: v2 extension search failed for query='{query}'.");
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
                Trace.WriteLine($"NetDocuments workspace search: endpoint='{path}' returned {items.Count} item(s).");
                if (items.Count > 0)
                {
                    results.AddRange(items);
                    break;
                }
            }
            catch
            {
                Trace.WriteLine($"NetDocuments workspace search: endpoint='{path}' failed.");
                // Continue to endpoint fallback.
            }
        }

        return results
            .OrderBy(item => item.WorkspaceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<NdLookupValueItem>> SearchLookupValuesAsync(
        string repositoryId,
        int attrNum,
        string term,
        int top = 50,
        bool extendedFiltering = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) || attrNum <= 0)
        {
            return Array.Empty<NdLookupValueItem>();
        }

        var escapedRepository = Uri.EscapeDataString(repositoryId);
        var safeTerm = NormalizeLookupTerm(term);
        var basePath =
            $"/v1/attributes/{escapedRepository}/{attrNum}?$select=key,description,closed,parent,parentDesc,defaulting,dynamicAttrs&$top={Math.Max(1, top)}";

        if (!string.IsNullOrWhiteSpace(safeTerm))
        {
            var escapedQuotedFilter = Uri.EscapeDataString($"substringof('{safeTerm.Replace("'", "''", StringComparison.Ordinal)}',keyfirst)");
            var escapedUnquotedFilter = Uri.EscapeDataString($"substringof({safeTerm},keyfirst)");
            var preferredPath = $"{basePath}&$filter={escapedUnquotedFilter}" +
                                (extendedFiltering ? "&useLongName=true&useExtendedFiltering=true" : string.Empty);
            var fallbackPath = $"{basePath}&$filter={escapedQuotedFilter}" +
                               (extendedFiltering ? "&useLongName=true&useExtendedFiltering=true" : string.Empty);
            try
            {
                using var document = await _apiClient.GetJsonAsync(preferredPath, cancellationToken);
                var parsed = ParseLookupRows(document.RootElement);
                Trace.WriteLine($"NetDocuments lookup search: endpoint='{preferredPath}' count={parsed.Count}.");
                return parsed;
            }
            catch
            {
                Trace.WriteLine($"NetDocuments lookup search: endpoint='{preferredPath}' failed.");
            }

            try
            {
                using var document = await _apiClient.GetJsonAsync(fallbackPath, cancellationToken);
                var parsed = ParseLookupRows(document.RootElement);
                Trace.WriteLine($"NetDocuments lookup search: endpoint='{fallbackPath}' count={parsed.Count}.");
                return parsed;
            }
            catch
            {
                Trace.WriteLine($"NetDocuments lookup search: endpoint='{fallbackPath}' failed.");
            }

            return Array.Empty<NdLookupValueItem>();
        }

        var recentPath = basePath + "&$filter=recent";
        try
        {
            using var document = await _apiClient.GetJsonAsync(recentPath, cancellationToken);
            var parsed = ParseLookupRows(document.RootElement);
            Trace.WriteLine($"NetDocuments lookup search: endpoint='{recentPath}' count={parsed.Count}.");
            return parsed;
        }
        catch
        {
            Trace.WriteLine($"NetDocuments lookup search: endpoint='{recentPath}' failed.");
        }

        return Array.Empty<NdLookupValueItem>();
    }

    public async Task<IReadOnlyList<NdLookupValueItem>> GetChildLookupValuesAsync(
        string repositoryId,
        int childAttrNum,
        string parentKey,
        string? term = null,
        int top = 50,
        bool includeUnfilteredFallback = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) || childAttrNum <= 0 || string.IsNullOrWhiteSpace(parentKey))
        {
            return Array.Empty<NdLookupValueItem>();
        }

        var escapedRepository = Uri.EscapeDataString(repositoryId);
        var escapedParent = Uri.EscapeDataString(parentKey);
        var basePath =
            $"/v1/attributes/{escapedRepository}/{childAttrNum}/{escapedParent}?$select=key,description,closed,parent,parentDesc,defaulting,dynamicAttrs&$top={Math.Max(1, top)}";
        if (!string.IsNullOrWhiteSpace(term))
        {
            var safeTerm = NormalizeLookupTerm(term);
            var escapedQuotedFilter = Uri.EscapeDataString($"substringof('{safeTerm.Replace("'", "''", StringComparison.Ordinal)}',keyfirst)");
            var escapedUnquotedFilter = Uri.EscapeDataString($"substringof({safeTerm},keyfirst)");
            var preferredPath = $"{basePath}&$filter={escapedUnquotedFilter}&useLongName=true&useExtendedFiltering=true";
            var fallbackPath = $"{basePath}&$filter={escapedQuotedFilter}&useLongName=true&useExtendedFiltering=true";
            try
            {
                using var lookupDocument = await _apiClient.GetJsonAsync(preferredPath, cancellationToken);
                var parsed = ParseLookupRows(lookupDocument.RootElement);
                Trace.WriteLine($"NetDocuments child lookup: endpoint='{preferredPath}' count={parsed.Count}.");
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
            catch
            {
                Trace.WriteLine($"NetDocuments child lookup: endpoint='{preferredPath}' failed.");
            }

            try
            {
                using var lookupDocument = await _apiClient.GetJsonAsync(fallbackPath, cancellationToken);
                var parsed = ParseLookupRows(lookupDocument.RootElement);
                Trace.WriteLine($"NetDocuments child lookup: endpoint='{fallbackPath}' count={parsed.Count}.");
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
            catch
            {
                Trace.WriteLine($"NetDocuments child lookup: endpoint='{fallbackPath}' failed.");
            }

            if (includeUnfilteredFallback)
            {
                try
                {
                    using var baseLookupDocument = await _apiClient.GetJsonAsync(basePath, cancellationToken);
                    var baseParsed = ParseLookupRows(baseLookupDocument.RootElement);
                    Trace.WriteLine($"NetDocuments child lookup: endpoint='{basePath}' count={baseParsed.Count}.");
                    if (baseParsed.Count > 0)
                    {
                        return baseParsed;
                    }
                }
                catch
                {
                    Trace.WriteLine($"NetDocuments child lookup: endpoint='{basePath}' failed.");
                }
            }

            return Array.Empty<NdLookupValueItem>();
        }

        using var baseDocument = await _apiClient.GetJsonAsync(basePath, cancellationToken);
        return ParseLookupRows(baseDocument.RootElement);
    }

    public async Task<IReadOnlyList<NdLookupValueItem>> GetRecentChildLookupValuesAsync(
        string repositoryId,
        int childAttrNum,
        string parentKey,
        int top = 25,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) || childAttrNum <= 0 || string.IsNullOrWhiteSpace(parentKey))
        {
            return Array.Empty<NdLookupValueItem>();
        }

        var escapedRepository = Uri.EscapeDataString(repositoryId);
        var escapedParent = Uri.EscapeDataString(parentKey);
        var path =
            $"/v1/attributes/{escapedRepository}/{childAttrNum}/{escapedParent}?$select=key,description,closed,parent,parentDesc,defaulting,dynamicAttrs&$filter=recent&$top={Math.Max(1, top)}";
        using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
        return ParseLookupRows(document.RootElement);
    }

    public async Task UpdateRecentLookupSelectionAsync(
        string repositoryId,
        int attrNum,
        string key,
        int? parentAttrNum = null,
        string? parentKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) || attrNum <= 0 || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var escapedRepository = Uri.EscapeDataString(repositoryId);
        using var content = new MultipartFormDataContent
        {
            { new StringContent("recent"), "updateMode" },
            { new StringContent(attrNum.ToString()), "attrNum" },
            { new StringContent(key), "key" }
        };

        if (parentAttrNum.HasValue && !string.IsNullOrWhiteSpace(parentKey))
        {
            content.Add(new StringContent(parentAttrNum.Value.ToString()), "parentAttrNum");
            content.Add(new StringContent(parentKey), "parentKey");
        }

        await _apiClient.PostAsync($"/v1/attributes/{escapedRepository}", content, cancellationToken);
    }

    public async Task<string?> ResolveWorkspaceEnvIdAsync(
        string cabinetId,
        string parentKey,
        string childKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cabinetId) ||
            string.IsNullOrWhiteSpace(parentKey) ||
            string.IsNullOrWhiteSpace(childKey))
        {
            return null;
        }

        var escapedCabinet = Uri.EscapeDataString(cabinetId);
        var escapedParent = Uri.EscapeDataString(parentKey);
        var escapedChild = Uri.EscapeDataString(childKey);
        var path = $"/v1/workspace/{escapedCabinet}/{escapedParent}/{escapedChild}/wsurl";

        using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var dataNode) &&
            dataNode.ValueKind is JsonValueKind.Object or JsonValueKind.String)
        {
            root = dataNode;
        }
        var raw = ReadString(root, "wsurl", "url", "value", "path", "id");
        if (string.IsNullOrWhiteSpace(raw) && root.ValueKind == JsonValueKind.String)
        {
            raw = root.GetString() ?? string.Empty;
        }
        var normalized = NormalizeWorkspaceEnvId(raw);
        var candidates = BuildContainerIdCandidates(raw, normalized).ToList();
        Trace.WriteLine(
            $"NetDocuments wsurl resolve: cabinet='{cabinetId}' parent='{parentKey}' child='{childKey}' raw='{raw}' normalized='{normalized}' candidates='{string.Join(",", candidates)}'.");

        foreach (var candidate in candidates)
        {
            try
            {
                var encoded = EncodeContainerIdForPath(candidate);
                using var _ = await _apiClient.GetJsonAsync($"/v2/container/{encoded}/info", cancellationToken);
                Trace.WriteLine($"NetDocuments wsurl resolve: accepted container id='{candidate}'.");
                return candidate;
            }
            catch
            {
                Trace.WriteLine($"NetDocuments wsurl resolve: container id probe failed id='{candidate}'.");
            }
        }

        return normalized;
    }

    public async Task<NdContainerNode?> GetContainerInfoAsync(
        string envId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(envId))
        {
            return null;
        }

        var encoded = EncodeContainerIdForPath(envId);
        var select = Uri.EscapeDataString("StandardAttributes,CustomAttributes,StatusAttributes,ContainerInfo,DeletedStatus,Descriptions,IncludeAcls,Ancestors,DispNames,Locations,Sync,Hold,UseLongName");
        using var document = await _apiClient.GetJsonAsync($"/v2/container/{encoded}/info?select={select}&options=AddToRecents", cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var dataNode) &&
            dataNode.ValueKind == JsonValueKind.Object)
        {
            root = dataNode;
        }

        return ParseContainerNode(root);
    }

    public async Task<string> GetContainerAncestryAsync(
        string envId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(envId))
        {
            return string.Empty;
        }

        var encoded = EncodeContainerIdForPath(envId);
        using var document = await _apiClient.GetJsonAsync($"/v2/container/{encoded}/ancestry", cancellationToken);
        var labels = EnumerateArray(document.RootElement)
            .Select(item => ReadString(item, "name", "description", "title"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        return labels.Count == 0 ? string.Empty : string.Join(" / ", labels);
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
        var encodedTarget = EncodeContainerIdForPath(targetId);
        foreach (var path in new[]
                 {
                     $"/v2/container/{encodedTarget}/ancestry",
                     $"/v2/container/{encodedTarget}/info",
                     $"/v1/Cabinet/{escapedCabinet}/containers/{Uri.EscapeDataString(targetId)}",
                     $"/v1/Container/{Uri.EscapeDataString(targetId)}/info"
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

        var extension = ReadExtensionValue(element);
        var rawType = string.IsNullOrWhiteSpace(extension)
            ? ReadString(element, "type", "containerType", "kind", "extension", "ext")
            : extension;
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
            ParentWorkspaceId = ReadString(element, "parentWorkspaceId", "workspaceId", "parentId", "workspace"),
            Extension = extension,
            SourceFlow = NdTargetSourceFlow.Browse
        };
    }

    private static NdTargetSelection? ParseWorkspaceSelection(JsonElement element)
    {
        var source = element;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "standardAttributes", "StandardAttributes", "attributes", "Attributes" })
            {
                if (element.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.Object)
                {
                    source = node;
                    break;
                }
            }
        }

        var id = ReadString(source, "id", "containerId", "workspaceId", "folderId");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = ReadString(element, "id", "containerId", "workspaceId", "folderId");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var extension = ReadString(source, "extension", "ext", "Ext");
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ReadString(element, "extension", "ext", "Ext");
        }

        var rawType = string.IsNullOrWhiteSpace(extension)
            ? ReadString(source, "type", "containerType", "kind")
            : extension;
        if (string.IsNullOrWhiteSpace(rawType))
        {
            rawType = ReadString(element, "type", "containerType", "kind");
        }

        var normalizedType = NdTargetBrowserLogic.NormalizeSupportedType(rawType, hasWorkspaceIdHint: true) ?? NdTargetType.Workspace;
        var name = ReadString(source, "name", "description", "label", "title");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadString(element, "name", "description", "label", "title");
        }

        return new NdTargetSelection
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            Type = normalizedType,
            ParentWorkspaceId = ReadString(source, "parentWorkspaceId", "workspaceId", "workspace", "parentId"),
            Extension = extension,
            SourceFlow = NdTargetSourceFlow.Browse
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


    private static IEnumerable<string> BuildFavoriteEndpointCandidates(string cabinetId)
    {
        var escaped = Uri.EscapeDataString(cabinetId);
        yield return $"/v1/User/favoriteLocations?cabinetId={escaped}";
        yield return $"/v1/User/favorites?cabinetId={escaped}";
        yield return $"/v1/Cabinet/{escaped}/favoriteLocations";
    }

    private async Task<IReadOnlyList<NdTargetRecentItem>> GetWorkspaceListAsync(
        string endpointPath,
        string? cabinetId,
        bool bypassCache,
        bool parseFavoriteShape,
        CancellationToken cancellationToken)
    {
        var primaryPath = BuildWorkspaceListPath(endpointPath, cabinetId, bypassCache, useQuotedCabinetFilter: true);
        try
        {
            using var document = await _apiClient.GetJsonAsync(primaryPath, cancellationToken);
            var parsed = ParseWorkspaceListItems(document.RootElement, parseFavoriteShape);
            Trace.WriteLine($"NetDocuments target browser: endpoint='{primaryPath}' count={parsed.Count}.");
            if (parsed.Count == 0)
            {
                Trace.WriteLine($"NetDocuments target browser: endpoint='{primaryPath}' shape={DescribeJsonShape(document.RootElement)}");
            }
            if (parsed.Count > 0 || string.IsNullOrWhiteSpace(cabinetId))
            {
                return parsed;
            }
        }
        catch
        {
            // Try fallback filter formatting only if cabinet filtering was requested.
            if (string.IsNullOrWhiteSpace(cabinetId))
            {
                throw;
            }
        }

        var fallbackPath = BuildWorkspaceListPath(endpointPath, cabinetId, bypassCache, useQuotedCabinetFilter: false);
        using (var document = await _apiClient.GetJsonAsync(fallbackPath, cancellationToken))
        {
            var parsed = ParseWorkspaceListItems(document.RootElement, parseFavoriteShape);
            Trace.WriteLine($"NetDocuments target browser: endpoint='{fallbackPath}' count={parsed.Count}.");
            if (parsed.Count == 0)
            {
                Trace.WriteLine($"NetDocuments target browser: endpoint='{fallbackPath}' shape={DescribeJsonShape(document.RootElement)}");
            }
            if (parsed.Count > 0 || string.IsNullOrWhiteSpace(cabinetId))
            {
                return parsed;
            }
        }

        // Cabinet scoping can legitimately return no rows depending on tenant API behavior.
        // Fall back to unfiltered user list so tabs remain useful.
        var unfilteredPath = BuildWorkspaceListPath(endpointPath, null, bypassCache, useQuotedCabinetFilter: false);
        using (var document = await _apiClient.GetJsonAsync(unfilteredPath, cancellationToken))
        {
            var parsed = ParseWorkspaceListItems(document.RootElement, parseFavoriteShape);
            Trace.WriteLine($"NetDocuments target browser: endpoint='{unfilteredPath}' count={parsed.Count}.");
            if (parsed.Count > 0)
            {
                return parsed;
            }

            Trace.WriteLine($"NetDocuments target browser: endpoint='{unfilteredPath}' shape={DescribeJsonShape(document.RootElement)}");
        }

        var v2Fallback = await GetWorkspaceListFromV2Async(cabinetId, parseFavoriteShape, cancellationToken);
        if (v2Fallback.Count > 0)
        {
            return v2Fallback;
        }

        return Array.Empty<NdTargetRecentItem>();
    }

    private static string BuildWorkspaceListPath(
        string endpointPath,
        string? cabinetId,
        bool bypassCache,
        bool useQuotedCabinetFilter)
    {
        var queryParts = new List<string>
        {
            "$select=standardAttributes"
        };

        if (!string.IsNullOrWhiteSpace(cabinetId))
        {
            var filterText = useQuotedCabinetFilter
                ? $"cabinet eq '{cabinetId.Trim().Replace("'", "''", StringComparison.Ordinal)}'"
                : $"cabinet eq {cabinetId.Trim()}";
            queryParts.Add($"$filter={Uri.EscapeDataString(filterText)}");
        }

        if (bypassCache)
        {
            queryParts.Add("bypasscache=true");
        }

        return $"{endpointPath}?{string.Join("&", queryParts)}";
    }

    private static IReadOnlyList<NdTargetRecentItem> ParseWorkspaceListItems(JsonElement root, bool parseFavoriteShape)
    {
        var results = new List<NdTargetRecentItem>();
        foreach (var item in EnumerateWorkspaceListItems(root))
        {
            var parsed = parseFavoriteShape
                ? ParseFavoriteWorkspaceAsRecent(item)
                : ParseRecentWorkspaceItem(item);
            if (parsed is not null)
            {
                results.Add(parsed);
            }
        }

        return results;
    }

    private static IEnumerable<JsonElement> EnumerateWorkspaceListItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[]
                     {
                         "wsRecent", "wsFav", "recent", "favorites", "locations",
                         "standardList",
                         "rows", "data", "items", "results", "value", "documents"
                     })
            {
                if (!TryGetPropertyIgnoreCase(root, name, out var child))
                {
                    continue;
                }

                if (child.ValueKind == JsonValueKind.Array)
                {
                    return child.EnumerateArray();
                }

                if (child.ValueKind == JsonValueKind.Object)
                {
                    var nested = EnumerateWorkspaceListItems(child);
                    if (nested.Any())
                    {
                        return nested;
                    }
                }
            }
        }

        return Array.Empty<JsonElement>();
    }

    private async Task<IReadOnlyList<NdTargetRecentItem>> GetWorkspaceListFromV2Async(
        string? cabinetId,
        bool parseFavoriteShape,
        CancellationToken cancellationToken)
    {
        var candidates = parseFavoriteShape
            ? BuildFavoriteEndpointCandidatesV2(cabinetId)
            : BuildRecentEndpointCandidatesV2(cabinetId);

        foreach (var path in candidates)
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                var parsed = ParseWorkspaceListItemsFromV2(document.RootElement);
                Trace.WriteLine($"NetDocuments target browser: v2 endpoint='{path}' count={parsed.Count}.");
                if (parsed.Count > 0)
                {
                    return parsed;
                }

                Trace.WriteLine($"NetDocuments target browser: v2 endpoint='{path}' shape={DescribeJsonShape(document.RootElement)}");
            }
            catch
            {
                Trace.WriteLine($"NetDocuments target browser: v2 endpoint='{path}' failed.");
            }
        }

        return Array.Empty<NdTargetRecentItem>();
    }

    private static IEnumerable<string> BuildRecentEndpointCandidatesV2(string? cabinetId)
    {
        if (!string.IsNullOrWhiteSpace(cabinetId))
        {
            var escaped = Uri.EscapeDataString(cabinetId);
            yield return $"/v2/user/recent/locations/{escaped}";
        }

        yield return "/v2/user/recent/locations";
    }

    private static IEnumerable<string> BuildFavoriteEndpointCandidatesV2(string? cabinetId)
    {
        if (!string.IsNullOrWhiteSpace(cabinetId))
        {
            var escaped = Uri.EscapeDataString(cabinetId);
            yield return $"/v2/user/favorites/{escaped}";
        }

        yield return "/v2/user/favorites";
    }

    private static IReadOnlyList<NdTargetRecentItem> ParseWorkspaceListItemsFromV2(JsonElement root)
    {
        var items = new List<NdTargetRecentItem>();
        foreach (var element in EnumerateSearchItems(root))
        {
            var selection = ParseTargetSelection(element) ?? ParseWorkspaceSelection(element);
            if (selection is null)
            {
                continue;
            }

            items.Add(new NdTargetRecentItem
            {
                Selection = selection,
                LastUsedUtc = DateTime.UtcNow,
                Source = NdTargetSource.Server
            });
        }

        return items;
    }

    private static string DescribeJsonShape(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return $"array(len={root.GetArrayLength()})";
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return root.ValueKind.ToString();
        }

        var names = root.EnumerateObject().Select(p => p.Name).Take(20).ToArray();
        return $"object(keys=[{string.Join(",", names)}])";
    }

    private static NdTargetRecentItem? ParseRecentWorkspaceItem(JsonElement element)
    {
        var selection = ParseWorkspaceSelection(element);
        if (selection is null)
        {
            return null;
        }

        var timestampRaw = ReadString(element, "lastUsedUtc", "lastAccessedUtc", "timestamp", "updatedUtc");
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

    private static NdTargetRecentItem? ParseFavoriteWorkspaceAsRecent(JsonElement element)
    {
        var selection = ParseWorkspaceSelection(element);
        if (selection is null)
        {
            return null;
        }

        var timestampRaw = ReadString(element, "pinnedUtc", "createdUtc", "timestamp", "updatedUtc");
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

        var extension = ReadExtensionValue(element);
        var rawType = string.IsNullOrWhiteSpace(extension)
            ? ReadString(element, "type", "containerType", "kind", "extension", "ext")
            : extension;
        var supportedType = NdTargetBrowserLogic.NormalizeSupportedType(rawType, element.TryGetProperty("workspaceId", out _));
        var name = ReadString(element, "name", "description", "label", "title");

        return new NdContainerNode
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            TypeRaw = rawType,
            Extension = extension,
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
        var filter = Uri.EscapeDataString($"extension eq '{extension}'");

        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(query))
        {
            candidates.Add($"/v2/search/{escapedCabinet}?q={escapedQuery}&top={top}&filter={filter}&filtertype=IncludeOnly&listflags=FoldersOnly,ValidateWorkspaces");
            candidates.Add($"/v2/search?cabinets={escapedCabinet}&q={escapedQuery}&top={top}&filter={filter}&filtertype=IncludeOnly&listflags=FoldersOnly,ValidateWorkspaces");
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
                var beforeCount = results.Count;
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
                var added = results.Count - beforeCount;
                Trace.WriteLine($"NetDocuments target search by extension: endpoint='{path}' extension='{extension}' added={added} total={results.Count}.");

                if (results.Count > 0)
                {
                    break;
                }
            }
            catch
            {
                Trace.WriteLine($"NetDocuments target search by extension: endpoint='{path}' extension='{extension}' failed.");
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
                if (TryGetPropertyIgnoreCase(root, name, out var child) && child.ValueKind == JsonValueKind.Array)
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

    private static IReadOnlyList<NdLookupValueItem> ParseLookupRows(JsonElement root)
    {
        var rows = EnumerateLookupRows(root);
        return rows
            .Select(item => new NdLookupValueItem
            {
                Key = ReadString(item, "key", "id", "value"),
                Description = ReadString(item, "description", "label", "name", "longName"),
                Closed = ReadBool(item, "closed"),
                ParentKey = ReadString(item, "parent", "parentKey"),
                ParentDescription = ReadString(item, "parentDesc", "parentDescription")
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToList();
    }

    private static IEnumerable<JsonElement> EnumerateLookupRows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "rows", "data", "items", "results", "value" })
            {
                if (root.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Array)
                {
                    return child.EnumerateArray();
                }
            }
        }

        return Array.Empty<JsonElement>();
    }

    private static string? NormalizeWorkspaceEnvId(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var trimmed = rawValue.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            var queryToken = TryReadContainerIdFromQuery(absolute.Query);
            if (!string.IsNullOrWhiteSpace(queryToken))
            {
                return NormalizeContainerIdToken(queryToken);
            }

            var segments = absolute.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
            {
                return NormalizeContainerIdToken(segments[^1]);
            }
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var candidate = parts.Length == 0 ? trimmed : parts[^1];
        return NormalizeContainerIdToken(candidate);
    }

    private static IEnumerable<string> BuildContainerIdCandidates(string? rawWsUrl, string? token)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var colonId = ConvertWsUrlPathToColonContainerId(rawWsUrl);
        if (!string.IsNullOrWhiteSpace(colonId) && seen.Add(colonId))
        {
            yield return colonId;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            yield break;
        }

        var normalized = NormalizeContainerIdToken(token);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        foreach (var candidate in new[]
                 {
                     normalized,
                     $"^{normalized}",
                     $"{normalized}.nev",
                     $"^{normalized}.nev"
                 })
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static string? ConvertWsUrlPathToColonContainerId(string? rawWsUrl)
    {
        if (string.IsNullOrWhiteSpace(rawWsUrl))
        {
            return null;
        }

        var value = rawWsUrl.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            value = absolute.AbsolutePath;
        }

        if (!value.StartsWith("/", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        return ":" + string.Join(":", segments);
    }

    private static string EncodeContainerIdForPath(string containerId)
    {
        var trimmed = containerId.Trim();
        if (trimmed.StartsWith(":", StringComparison.Ordinal))
        {
            var segments = trimmed.Split(':');
            var encodedSegments = new List<string>(segments.Length);
            foreach (var segment in segments)
            {
                if (segment.Length == 0)
                {
                    encodedSegments.Add(string.Empty);
                    continue;
                }

                var decoded = Uri.UnescapeDataString(segment);
                encodedSegments.Add(Uri.EscapeDataString(decoded));
            }

            return string.Join(":", encodedSegments);
        }

        return Uri.EscapeDataString(trimmed);
    }

    private static string? TryReadContainerIdFromQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var queryText = query.TrimStart('?');
        var parts = queryText.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                continue;
            }

            var key = pair[0];
            if (!key.Equals("id", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("container", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("containerid", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("workspace", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("workspaceid", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(pair[1]);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string NormalizeContainerIdToken(string token)
    {
        var normalized = token.Trim();
        var queryIndex = normalized.IndexOfAny(new[] { '?', '#' });
        if (queryIndex >= 0)
        {
            normalized = normalized[..queryIndex];
        }

        if (normalized.StartsWith("^", StringComparison.Ordinal))
        {
            normalized = normalized[1..];
        }

        if (normalized.EndsWith(".nev", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized;
    }

    private static string NormalizeLookupTerm(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return string.Empty;
        }

        return term
            .Trim()
            .Replace("*", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string ReadExtensionValue(JsonElement element)
    {
        var extension = ReadString(element, "extension", "ext", "Ext");
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return extension;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "attributes", "Attributes", "profile", "Profile" })
            {
                if (element.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.Object)
                {
                    extension = ReadString(node, "extension", "ext", "Ext", "type", "Type");
                    if (!string.IsNullOrWhiteSpace(extension))
                    {
                        return extension;
                    }
                }
            }
        }

        return string.Empty;
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
