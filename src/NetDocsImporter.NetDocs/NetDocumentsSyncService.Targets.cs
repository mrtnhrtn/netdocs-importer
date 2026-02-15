using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NetDocsImporter.Core;

namespace NetDocsImporter.NetDocs;

/// <summary>
/// Provides target browsing, lookup resolution, and target profile snapshot operations for NetDocuments containers.
/// </summary>
public sealed partial class NetDocumentsSyncService
{
    private static readonly HashSet<string> HiddenCabinetPseudoContainerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cabinet",
        "Inbox",
        "Documents"
    };

    private enum TargetDefaultsSource
    {
        V1Endpoints,
        WorkspaceLookupContext,
        V2ContainerInfo,
        None
    }

    private readonly record struct TargetDefaultsResolutionResult(
        EffectiveProfileDefaults Defaults,
        TargetDefaultsSource Source);

    /// <summary>
    /// Retrieves supported destination container selections for a cabinet.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier used to scope target discovery.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Container selections ordered by type and display name.</returns>
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

    /// <summary>
    /// Gets recent targets for the current user, optionally filtered by cabinet.
    /// </summary>
    /// <param name="cabinetId">Optional cabinet identifier filter.</param>
    /// <param name="bypassCache"><see langword="true"/> to request server-side cache bypass when supported.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Recent target entries in server-defined order.</returns>
    public async Task<IReadOnlyList<NdTargetRecentItem>> GetRecentTargetsAsync(
        string? cabinetId = null,
        bool bypassCache = false,
        CancellationToken cancellationToken = default)
    {
        return await GetWorkspaceListAsync("/v1/User/wsRecent", cabinetId, bypassCache, parseFavoriteShape: false, cancellationToken);
    }

    /// <summary>
    /// Gets favorite targets for the current user, optionally filtered by cabinet.
    /// </summary>
    /// <param name="cabinetId">Optional cabinet identifier filter.</param>
    /// <param name="bypassCache"><see langword="true"/> to request server-side cache bypass when supported.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Favorite target entries mapped to favorite-specific view models.</returns>
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

    /// <summary>
    /// Searches workspace targets using the active tenant search behavior.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier used to scope the search.</param>
    /// <param name="query">Search text entered by the user.</param>
    /// <param name="top">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Workspace search results ordered by workspace name.</returns>
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

        var results = new List<NdWorkspaceSearchResult>();

        try
        {
            var items = await SearchTargetSelectionsByExtensionAsync(
                cabinetId,
                "ndws",
                query.Trim(),
                cancellationToken,
                Math.Max(10, top));
            await HydrateTargetSelectionNamesAsync(items, cancellationToken);
            Trace.WriteLine($"ND-SEARCH workspace v2-extension query='{query}' count={items.Count}.");
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
            Trace.WriteLine($"ND-SEARCH workspace v2-extension failed query='{query}'.");
        }

        Trace.WriteLine("ND-SEARCH workspace fallback-disabled reason='tenant stability; removed known dead v1 fallback endpoints'.");

        return results
            .OrderBy(item => item.WorkspaceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task HydrateTargetSelectionNamesAsync(
        IReadOnlyList<NdTargetSelection> selections,
        CancellationToken cancellationToken)
    {
        if (selections.Count == 0)
        {
            return;
        }

        var hydratedNameCache = new Dictionary<string, NdContainerNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections)
        {
            if (selection is null ||
                string.IsNullOrWhiteSpace(selection.Id) ||
                !ShouldHydrateSelectionName(selection))
            {
                continue;
            }

            if (!hydratedNameCache.TryGetValue(selection.Id, out var hydrated))
            {
                hydrated = await TryHydrateContainerNodeAsync(
                    selection.Id,
                    parentContainerId: null,
                    selection.Type,
                    cancellationToken);
                hydratedNameCache[selection.Id] = hydrated;
            }

            if (hydrated is not null && !string.IsNullOrWhiteSpace(hydrated.Name))
            {
                selection.Name = hydrated.Name;
                if (string.IsNullOrWhiteSpace(selection.ParentWorkspaceId) &&
                    !string.IsNullOrWhiteSpace(hydrated.ParentWorkspaceId))
                {
                    selection.ParentWorkspaceId = hydrated.ParentWorkspaceId;
                }

                if (NdTargetBrowserLogic.IsCollabspaceIdentifier(hydrated.Id) &&
                    !NdTargetBrowserLogic.IsCollabspaceIdentifier(selection.Id))
                {
                    selection.Id = hydrated.Id;
                    if (string.IsNullOrWhiteSpace(selection.Extension) &&
                        !string.IsNullOrWhiteSpace(hydrated.Extension))
                    {
                        selection.Extension = hydrated.Extension;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Searches lookup values for a specific attribute.
    /// </summary>
    /// <param name="repositoryId">Repository identifier that owns the attribute.</param>
    /// <param name="attrNum">Lookup attribute number.</param>
    /// <param name="term">Search term; when empty recent values are requested.</param>
    /// <param name="top">Maximum number of values to return.</param>
    /// <param name="extendedFiltering"><see langword="true"/> to request extended NetDocuments filtering options.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Matching lookup values.</returns>
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

    /// <summary>
    /// Retrieves child lookup values for a parent key in a parent-child lookup.
    /// </summary>
    /// <param name="repositoryId">Repository identifier that owns the attributes.</param>
    /// <param name="childAttrNum">Child lookup attribute number.</param>
    /// <param name="parentKey">Selected parent lookup key.</param>
    /// <param name="term">Optional child search term.</param>
    /// <param name="top">Maximum number of values to return.</param>
    /// <param name="includeUnfilteredFallback"><see langword="true"/> to retry without filter when filtered endpoints return no rows.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Matching child lookup values.</returns>
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

    /// <summary>
    /// Retrieves recent child lookup values for a selected parent key.
    /// </summary>
    /// <param name="repositoryId">Repository identifier that owns the attributes.</param>
    /// <param name="childAttrNum">Child lookup attribute number.</param>
    /// <param name="parentKey">Selected parent lookup key.</param>
    /// <param name="top">Maximum number of values to return.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Recent child lookup values.</returns>
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

    /// <summary>
    /// Updates server-side "recent lookup" ordering after the user selects a lookup key.
    /// </summary>
    /// <param name="repositoryId">Repository identifier that owns the lookup attribute.</param>
    /// <param name="attrNum">Selected lookup attribute number.</param>
    /// <param name="key">Selected lookup key.</param>
    /// <param name="parentAttrNum">Optional parent attribute number for parent-child lookup updates.</param>
    /// <param name="parentKey">Optional parent lookup key for parent-child lookup updates.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
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

    /// <summary>
    /// Resolves a workspace environment identifier from parent and child lookup keys.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier that owns the workspace profile.</param>
    /// <param name="parentKey">Parent lookup key (for example client key).</param>
    /// <param name="childKey">Child lookup key (for example matter key).</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Resolved environment identifier when available; otherwise <see langword="null"/>.</returns>
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

    /// <summary>
    /// Retrieves container metadata by environment identifier.
    /// </summary>
    /// <param name="envId">Container environment identifier.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Container node details, or <see langword="null"/> when input is empty.</returns>
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

    /// <summary>
    /// Retrieves a displayable ancestry breadcrumb for a container.
    /// </summary>
    /// <param name="envId">Container environment identifier.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Resolved ancestry text, or an empty string when unavailable.</returns>
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

    /// <summary>
    /// Retrieves child containers under a parent container or workspace.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier used for API scoping.</param>
    /// <param name="parentContainerId">Optional parent container identifier.</param>
    /// <param name="workspaceId">Optional workspace identifier used when no explicit parent is selected.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Child container nodes sorted for target-browser presentation.</returns>
    public async Task<IReadOnlyList<NdContainerNode>> GetContainerChildrenAsync(
        string cabinetId,
        string? parentContainerId = null,
        string? workspaceId = null,
        NdTargetType? preferredType = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<NdContainerNode>();
        var searchScopeId = !string.IsNullOrWhiteSpace(parentContainerId)
            ? parentContainerId
            : workspaceId;
        string? resolvedSearchScopeId = null;
        if (!string.IsNullOrWhiteSpace(searchScopeId))
        {
            try
            {
                resolvedSearchScopeId = await ResolveContainerIdForBrowseAsync(searchScopeId, cancellationToken);
                var searchScopeCandidates = new List<string>();
                AddSearchScopeCandidate(searchScopeCandidates, resolvedSearchScopeId);
                AddSearchScopeCandidate(searchScopeCandidates, searchScopeId);
                foreach (var candidate in BuildContainerIdCandidates(searchScopeId, NormalizeWorkspaceEnvId(searchScopeId)))
                {
                    AddSearchScopeCandidate(searchScopeCandidates, candidate);
                }

                foreach (var scopeCandidate in searchScopeCandidates)
                {
                    var hydratedNameCache = new Dictionary<string, NdContainerNode?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var extension in new[] { "ndws", "ndflt", "ndfld" })
                    {
                        var items = await SearchTargetSelectionsByExtensionAsync(
                            cabinetId,
                            extension,
                            null,
                            cancellationToken,
                            top: 200,
                            containerId: scopeCandidate);
                        foreach (var item in items)
                        {
                            var node = new NdContainerNode
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
                            };

                            // Some search shapes return IDs but omit human display names.
                            if (string.IsNullOrWhiteSpace(node.Name) ||
                                string.Equals(node.Name, node.Id, StringComparison.OrdinalIgnoreCase) ||
                                !IsValidContainerDisplayName(node.Name) ||
                                LooksLikeContainerIdentifier(node.Name))
                            {
                                if (!hydratedNameCache.TryGetValue(node.Id, out var hydrated))
                                {
                                    hydrated = await TryHydrateContainerNodeAsync(
                                        node.Id,
                                        parentContainerId,
                                        node.SupportedType,
                                        cancellationToken);
                                    hydratedNameCache[node.Id] = hydrated;
                                }

                                if (hydrated is not null && !string.IsNullOrWhiteSpace(hydrated.Name))
                                {
                                    node.Name = hydrated.Name;
                                    node.PathDisplay = string.IsNullOrWhiteSpace(node.PathDisplay) ? hydrated.PathDisplay : node.PathDisplay;
                                    node.ParentWorkspaceId = string.IsNullOrWhiteSpace(node.ParentWorkspaceId) ? hydrated.ParentWorkspaceId : node.ParentWorkspaceId;
                                }
                            }

                            if (string.IsNullOrWhiteSpace(node.Name))
                            {
                                node.Name = node.Id;
                            }

                            if (ShouldRejectAmbiguousFolderNode(node.Id, node.SupportedType, node.Name))
                            {
                                continue;
                            }

                            results.Add(node);
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
            }
            catch
            {
                // Continue to endpoint fallback.
            }
        }

        var fallbackParentId = !string.IsNullOrWhiteSpace(parentContainerId)
            ? resolvedSearchScopeId ?? parentContainerId
            : parentContainerId;
        var fallbackWorkspaceId = !string.IsNullOrWhiteSpace(workspaceId)
            ? resolvedSearchScopeId ?? workspaceId
            : workspaceId;
        foreach (var path in BuildChildrenEndpointCandidates(cabinetId, fallbackParentId, fallbackWorkspaceId, preferredType))
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                var nodes = await ParseContainerChildrenAsync(
                    document.RootElement,
                    fallbackParentId,
                    cancellationToken);
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

    /// <summary>
    /// Retrieves top-level cabinet folders for tree root expansion.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier used for API scoping.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Top-level folder/workspace nodes sorted for target-browser presentation.</returns>
    public async Task<IReadOnlyList<NdContainerNode>> GetCabinetTopLevelFoldersAsync(
        string cabinetId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cabinetId))
        {
            return Array.Empty<NdContainerNode>();
        }

        var results = new List<NdContainerNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in BuildCabinetTopLevelFoldersEndpointCandidates(cabinetId))
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                var parsed = await ParseContainerChildrenAsync(document.RootElement, null, cancellationToken);
                foreach (var node in parsed)
                {
                    if (node.SupportedType == NdTargetType.WorkspaceFilter)
                    {
                        continue;
                    }

                    if (seen.Add(node.Id))
                    {
                        results.Add(node);
                    }
                }

                if (results.Count > 0)
                {
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

    /// <summary>
    /// Resolves a container identifier to a browse-friendly identifier accepted by v2 container-search endpoints.
    /// </summary>
    /// <param name="containerId">Raw container identifier from recent/favorite/workspace rows.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Resolved container identifier when possible; otherwise the original identifier.</returns>
    public async Task<string> ResolveContainerIdForBrowseAsync(
        string containerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return string.Empty;
        }

        var candidates = new List<string>();
        AddSearchScopeCandidate(candidates, containerId);
        foreach (var candidate in BuildContainerIdCandidates(containerId, NormalizeWorkspaceEnvId(containerId)))
        {
            AddSearchScopeCandidate(candidates, candidate);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var encoded = EncodeContainerIdForPath(candidate);
                using var document = await _apiClient.GetJsonAsync($"/v2/container/{encoded}/info", cancellationToken);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    TryGetPropertyIgnoreCase(root, "data", out var dataNode) &&
                    dataNode.ValueKind == JsonValueKind.Object)
                {
                    root = dataNode;
                }

                var resolved = ReadString(root, "id", "containerId", "workspaceId", "folderId", "envId", "environmentId");
                return string.IsNullOrWhiteSpace(resolved) ? candidate : resolved;
            }
            catch
            {
                // Continue candidate fallback.
            }
        }

        return containerId.Trim();
    }

    private async Task<List<NdContainerNode>> ParseContainerChildrenAsync(
        JsonElement root,
        string? parentContainerId,
        CancellationToken cancellationToken)
    {
        var nodes = new List<NdContainerNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hydratedNameCache = new Dictionary<string, NdContainerNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in EnumerateContainerChildrenItems(root))
        {
            var parsed = ParseContainerNode(item);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Id))
            {
                continue;
            }

            if (ShouldHydrateContainerNode(parsed))
            {
                if (!hydratedNameCache.TryGetValue(parsed.Id, out var hydrated))
                {
                    hydrated = await TryHydrateContainerNodeAsync(
                        parsed.Id,
                        parentContainerId,
                        parsed.SupportedType,
                        cancellationToken);
                    hydratedNameCache[parsed.Id] = hydrated;
                }

                if (hydrated is not null)
                {
                    parsed = hydrated;
                }
            }

            if (parsed.SupportedType is null)
            {
                continue;
            }

            NormalizeContainerNodeForTree(parsed, parentContainerId);
            if (ShouldRejectAmbiguousFolderNode(parsed.Id, parsed.SupportedType, parsed.Name))
            {
                continue;
            }
            if (ShouldSuppressCabinetPseudoContainer(parsed, parentContainerId))
            {
                continue;
            }

            if (seen.Add(parsed.Id))
            {
                nodes.Add(parsed);
            }
        }

        return nodes;
    }

    private static bool ShouldHydrateContainerNode(NdContainerNode node)
    {
        if (node.SupportedType is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(node.Name))
        {
            return true;
        }

        if (string.Equals(node.Name, node.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IsValidContainerDisplayName(node.Name))
        {
            return true;
        }

        return LooksLikeContainerIdentifier(node.Name);
    }

    private async Task<NdContainerNode?> TryHydrateContainerNodeAsync(
        string containerId,
        string? parentContainerId,
        NdTargetType? expectedType,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        AddSearchScopeCandidate(candidates, containerId);
        AddSearchScopeCandidate(candidates, TrimContainerIdVersionSuffix(containerId));
        foreach (var candidate in BuildContainerIdCandidates(containerId, NormalizeWorkspaceEnvId(containerId)))
        {
            AddSearchScopeCandidate(candidates, candidate);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var hydrated = await GetContainerInfoAsync(candidate, cancellationToken);
                if (hydrated is null)
                {
                    continue;
                }

                if (hydrated.SupportedType is null &&
                    expectedType.HasValue &&
                    !IsAmbiguousNevEnvelopeIdentifier(candidate) &&
                    !IsAmbiguousNevEnvelopeIdentifier(containerId) &&
                    !IsAmbiguousNevEnvelopeIdentifier(hydrated.Id))
                {
                    hydrated.SupportedType = expectedType;
                }
                hydrated.SupportedType ??= InferTargetTypeFromContainerId(candidate);
                hydrated.SupportedType ??= InferTargetTypeFromContainerId(containerId);
                hydrated.SupportedType ??= InferTargetTypeFromContainerId(hydrated.Id);
                if (hydrated.SupportedType is null)
                {
                    Trace.WriteLine(
                        $"NetDocuments target hydration skipped id='{containerId}' candidate='{candidate}' reason='unsupported-type' hydratedId='{hydrated.Id}'.");
                    continue;
                }

                if (LooksLikeContainerIdentifier(hydrated.Name))
                {
                    var ancestryName = await TryResolveDisplayNameFromAncestryAsync(hydrated.Id, cancellationToken)
                                      ?? await TryResolveDisplayNameFromAncestryAsync(candidate, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(ancestryName))
                    {
                        hydrated.Name = ancestryName;
                    }

                    if (LooksLikeContainerIdentifier(hydrated.Name))
                    {
                        Trace.WriteLine(
                            $"NetDocuments target hydration unresolved-name id='{containerId}' candidate='{candidate}' hydratedId='{hydrated.Id}' name='{hydrated.Name}'.");
                    }
                }

                NormalizeContainerNodeForTree(hydrated, parentContainerId);
                return hydrated;
            }
            catch
            {
                // Try the next candidate id.
            }
        }

        return null;
    }

    private async Task<string?> TryResolveDisplayNameFromAncestryAsync(string containerId, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        AddSearchScopeCandidate(candidates, containerId);
        AddSearchScopeCandidate(candidates, TrimContainerIdVersionSuffix(containerId));
        foreach (var candidate in BuildContainerIdCandidates(containerId, NormalizeWorkspaceEnvId(containerId)))
        {
            AddSearchScopeCandidate(candidates, candidate);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var ancestryPath = await GetContainerAncestryAsync(candidate, cancellationToken);
                if (string.IsNullOrWhiteSpace(ancestryPath))
                {
                    continue;
                }

                var segments = ancestryPath
                    .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (var index = segments.Length - 1; index >= 0; index--)
                {
                    var segment = segments[index];
                    if (!string.IsNullOrWhiteSpace(segment) &&
                        IsValidContainerDisplayName(segment) &&
                        !LooksLikeContainerIdentifier(segment))
                    {
                        return segment;
                    }
                }
            }
            catch
            {
                // Ignore ancestry fallback failures and keep trying candidates.
            }
        }

        return null;
    }

    private static string? TrimContainerIdVersionSuffix(string? containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return null;
        }

        var trimmed = containerId.Trim();
        var pipeIndex = trimmed.IndexOf('|');
        if (pipeIndex <= 0)
        {
            return null;
        }

        return trimmed[..pipeIndex];
    }

    private static void NormalizeContainerNodeForTree(NdContainerNode node, string? parentContainerId)
    {
        if (string.IsNullOrWhiteSpace(node.ParentId))
        {
            node.ParentId = parentContainerId ?? string.Empty;
        }

        node.IsSelectable = node.SupportedType.HasValue;
        node.UnsupportedReason = NdTargetBrowserLogic.GetUnsupportedReason(node.SupportedType);
        node.ChildrenLoadState = NdChildrenLoadState.NotLoaded;

        if (node.SupportedType == NdTargetType.WorkspaceFilter)
        {
            node.HasChildren = false;
            return;
        }

        if (node.SupportedType is NdTargetType.Workspace or NdTargetType.Folder && !node.HasChildren)
        {
            // Most legacy list endpoints omit hasChildren for folder/workspace rows.
            node.HasChildren = true;
        }
    }

    private static bool ShouldSuppressCabinetPseudoContainer(NdContainerNode node, string? parentContainerId)
    {
        if (!string.IsNullOrWhiteSpace(parentContainerId))
        {
            return false;
        }

        return ShouldSuppressCabinetPseudoContainer(node.Name, node.Id, node.SupportedType);
    }

    private static bool ShouldSuppressCabinetPseudoContainer(string? name, string? id, NdTargetType? supportedType)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(id) ||
            supportedType is null)
        {
            return false;
        }

        if (!HiddenCabinetPseudoContainerNames.Contains(name.Trim()))
        {
            return false;
        }

        if (!NdTargetBrowserLogic.IsCollabspaceIdentifier(id))
        {
            return false;
        }

        return supportedType is NdTargetType.Folder or NdTargetType.Workspace;
    }

    private static bool ShouldRejectAmbiguousFolderNode(string? id, NdTargetType? supportedType, string? displayName)
    {
        if (supportedType != NdTargetType.Folder || !IsAmbiguousNevEnvelopeIdentifier(id))
        {
            return false;
        }

        var effectiveName = displayName ?? string.Empty;
        return LooksLikeContainerIdentifier(effectiveName) || IsLikelyDocumentDisplayName(effectiveName);
    }

    /// <summary>
    /// Resolves a human-readable path for a selected target container.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier for legacy endpoint fallback.</param>
    /// <param name="targetId">Container identifier to resolve.</param>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>Display path when available; otherwise the raw target identifier.</returns>
    public async Task<string> ResolveTargetPathAsync(
        string cabinetId,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return string.Empty;
        }

        var encodedTarget = EncodeContainerIdForPath(targetId);
        foreach (var path in new[]
                 {
                     $"/v2/container/{encodedTarget}/ancestry",
                     $"/v2/container/{encodedTarget}/info",
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

    /// <summary>
    /// Builds a target profile snapshot containing profile attributes and resolved effective defaults.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier for metadata lookups.</param>
    /// <param name="repositoryId">Repository identifier used when falling back to cached metadata.</param>
    /// <param name="target">Selected target container.</param>
    /// <param name="lookupContext">Optional workspace lookup context used to synthesize defaults when endpoints are unavailable.</param>
    /// <param name="cancellationToken">Token used to cancel API/database work.</param>
    /// <returns>Snapshot payload used by UI and export/upload pipelines.</returns>
    public async Task<NdTargetProfileSnapshot> GetTargetProfileSnapshotAsync(
        string cabinetId,
        string repositoryId,
        NdTargetSelection target,
        WorkspaceLookupContext? lookupContext = null,
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

        var defaultsResolution = await TryFetchTargetDefaultsAsync(
            target,
            attributes,
            lookupContext,
            cancellationToken);
        var defaults = defaultsResolution.Defaults;
        await ResolveDefaultDisplayValuesAsync(cabinetId, attributes, defaults, cancellationToken);

        Trace.WriteLine(
            $"NetDocuments target profile sync: target={target.Type}:{target.Id}, attributes={attributes.Count}, defaults={defaults.ValuesByAttributeId.Count}");
        Trace.WriteLine(
            $"ND-PROFILE defaults source={defaultsResolution.Source} count={defaults.ValuesByAttributeId.Count} target={target.Id}");

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
        yield return $"/v1/Cabinet/{escaped}/folders";
    }

    private static NdTargetSelection? ParseTargetSelection(JsonElement element, string? defaultExtension = null)
    {
        var source = SelectTargetSelectionSource(element);
        var id = ResolveContainerIdentifier(element, source);

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var extension = ReadExtensionValue(source);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ReadExtensionValue(element);
        }

        var rawType = string.IsNullOrWhiteSpace(extension)
            ? ReadString(source, "type", "containerType", "kind", "extension", "ext")
            : extension;
        if (string.IsNullOrWhiteSpace(rawType))
        {
            rawType = ReadString(element, "type", "containerType", "kind", "extension", "ext");
        }

        var explicitContainerIdentifier = IsExplicitContainerIdentifier(id);
        var hasContainerTypeHint = IsContainerSelectionTypeToken(defaultExtension) ||
                                   IsContainerSelectionTypeToken(rawType) ||
                                   IsContainerSelectionTypeToken(extension);
        if ((!explicitContainerIdentifier && HasDocumentIdentifierHint(element, source, id) && !hasContainerTypeHint) ||
            IsDocumentLikeType(rawType) ||
            IsDocumentLikeType(extension))
        {
            return null;
        }

        var ambiguousNevIdentifier = IsAmbiguousNevEnvelopeIdentifier(id);
        var defaultExtensionNormalized = (defaultExtension ?? string.Empty).Trim();
        var allowAmbiguousDefaultType = string.Equals(defaultExtensionNormalized, "ndws", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(defaultExtensionNormalized, "ndflt", StringComparison.OrdinalIgnoreCase);
        var hasWorkspaceIdHint =
            TryGetPropertyIgnoreCase(source, "workspaceId", out _) ||
            TryGetPropertyIgnoreCase(element, "workspaceId", out _);
        var resolvedType = NdTargetBrowserLogic.NormalizeSupportedType(rawType, hasWorkspaceIdHint);
        if (resolvedType == NdTargetType.Folder &&
            ambiguousNevIdentifier &&
            string.IsNullOrWhiteSpace(rawType) &&
            string.IsNullOrWhiteSpace(extension))
        {
            resolvedType = null;
        }
        if (resolvedType is null && !string.IsNullOrWhiteSpace(defaultExtension))
        {
            if (!ambiguousNevIdentifier || allowAmbiguousDefaultType)
            {
                resolvedType = NdTargetBrowserLogic.NormalizeSupportedType(defaultExtension, hasWorkspaceIdHint);
            }
        }
        if (resolvedType is null)
        {
            resolvedType = InferTargetTypeFromContainerId(id);
        }

        if (resolvedType is null)
        {
            return null;
        }

        var name = ReadPreferredContainerName(element, source);
        var effectiveName = string.IsNullOrWhiteSpace(name) ? id : name;
        if (ShouldSuppressCabinetPseudoContainer(effectiveName, id, resolvedType))
        {
            return null;
        }

        var parentWorkspaceId = ReadString(source, "parentWorkspaceId", "workspaceId", "parentId", "workspace");
        if (string.IsNullOrWhiteSpace(parentWorkspaceId))
        {
            parentWorkspaceId = ReadString(element, "parentWorkspaceId", "workspaceId", "parentId", "workspace");
        }

        var effectiveExtension = extension;
        if (string.IsNullOrWhiteSpace(effectiveExtension) &&
            !string.IsNullOrWhiteSpace(defaultExtension) &&
            (!IsAmbiguousNevEnvelopeIdentifier(id) || allowAmbiguousDefaultType))
        {
            effectiveExtension = defaultExtension;
        }

        return new NdTargetSelection
        {
            Id = id,
            Name = effectiveName,
            Type = resolvedType.Value,
            ParentWorkspaceId = parentWorkspaceId,
            Extension = effectiveExtension,
            SourceFlow = NdTargetSourceFlow.Browse
        };
    }


    private static string ReadPreferredContainerName(JsonElement element, JsonElement source)
    {
        var candidates = new[]
        {
            "name", "displayName", "dispName", "longName", "shortName", "fullName", "description", "desc", "label", "title", "long", "short", "default"
        };

        foreach (var node in EnumerateCandidateNameNodes(element, source))
        {
            var value = ReadString(node, candidates);
            if (!IsValidContainerDisplayName(value) || LooksLikeContainerIdentifier(value))
            {
                continue;
            }

            return value;
        }

        foreach (var node in EnumerateFallbackNameNodes(element, source))
        {
            var value = TryFindPreferredNameRecursive(node, candidates);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<JsonElement> EnumerateCandidateNameNodes(JsonElement element, JsonElement source)
    {
        yield return element;
        if (!source.Equals(element))
        {
            yield return source;
        }

        foreach (var root in new[] { element, source })
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var key in new[]
                     {
                         "data", "item", "result", "value", "containerInfo", "container", "descriptions", "dispNames",
                         "standardAttributes", "attributes", "profile"
                     })
            {
                if (TryGetPropertyIgnoreCase(root, key, out var nested) && nested.ValueKind == JsonValueKind.Object)
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<JsonElement> EnumerateFallbackNameNodes(JsonElement element, JsonElement source)
    {
        foreach (var root in new[] { element, source })
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var key in new[] { "data", "item", "result", "value", "containerInfo", "container", "descriptions", "dispNames", "locations" })
            {
                if (TryGetPropertyIgnoreCase(root, key, out var nested))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string TryFindPreferredNameRecursive(JsonElement element, IReadOnlyList<string> candidates)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in candidates)
            {
                if (!TryGetPropertyIgnoreCase(element, candidate, out var valueNode))
                {
                    continue;
                }

                var value = valueNode.ValueKind switch
                {
                    JsonValueKind.String => valueNode.GetString() ?? string.Empty,
                    JsonValueKind.Number => valueNode.GetRawText(),
                    _ => string.Empty
                };

                if (IsValidContainerDisplayName(value) && !LooksLikeContainerIdentifier(value))
                {
                    return value.Trim();
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = TryFindPreferredNameRecursive(property.Value, candidates);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = TryFindPreferredNameRecursive(item, candidates);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return string.Empty;
    }

    private static bool IsValidContainerDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "nev", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.StartsWith(":", StringComparison.Ordinal) &&
            trimmed.IndexOf(".nev", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikeContainerIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.IndexOf(".nev", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (trimmed.StartsWith(":", StringComparison.Ordinal) &&
            trimmed.Count(c => c == ':') >= 3)
        {
            return true;
        }

        if (trimmed.IndexOf("^F", StringComparison.OrdinalIgnoreCase) >= 0 ||
            trimmed.IndexOf("^W", StringComparison.OrdinalIgnoreCase) >= 0 ||
            trimmed.IndexOf("^C", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (trimmed.Count(c => c == '-') >= 2 &&
            trimmed.Length >= 10 &&
            trimmed.All(ch => char.IsLetterOrDigit(ch) || ch == '-'))
        {
            return true;
        }

        return false;
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

        var id = ReadString(element, "id", "containerId", "workspaceId", "folderId");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = ReadString(source, "id", "containerId", "workspaceId", "folderId");
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

        var explicitContainerIdentifier = IsExplicitContainerIdentifier(id);
        var hasWorkspaceTypeHint = IsWorkspaceTypeToken(rawType) ||
                                   IsWorkspaceTypeToken(extension) ||
                                   InferTargetTypeFromContainerId(id) == NdTargetType.Workspace;
        if ((!explicitContainerIdentifier && HasDocumentIdentifierHint(element, source, id) && !hasWorkspaceTypeHint) ||
            IsDocumentLikeType(rawType) ||
            IsDocumentLikeType(extension))
        {
            return null;
        }

        var ambiguousNevIdentifier = IsAmbiguousNevEnvelopeIdentifier(id);
        var normalizedType = NdTargetBrowserLogic.NormalizeSupportedType(rawType, hasWorkspaceIdHint: true);
        if (normalizedType == NdTargetType.Folder &&
            ambiguousNevIdentifier &&
            string.IsNullOrWhiteSpace(rawType) &&
            string.IsNullOrWhiteSpace(extension))
        {
            normalizedType = null;
        }
        if (normalizedType is null)
        {
            normalizedType = InferTargetTypeFromContainerId(id);
        }
        if (normalizedType is null)
        {
            return null;
        }
        var name = ReadPreferredContainerName(element, source);
        var effectiveName = string.IsNullOrWhiteSpace(name) ? id : name;
        if (ShouldSuppressCabinetPseudoContainer(effectiveName, id, normalizedType))
        {
            return null;
        }

        return new NdTargetSelection
        {
            Id = id,
            Name = effectiveName,
            Type = normalizedType.Value,
            ParentWorkspaceId = ReadString(source, "parentWorkspaceId", "workspaceId", "workspace", "parentId"),
            Extension = extension,
            SourceFlow = NdTargetSourceFlow.Browse
        };
    }

    private static JsonElement SelectTargetSelectionSource(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return element;
        }

        foreach (var propertyName in new[] { "standardAttributes", "attributes", "profile", "containerInfo", "container" })
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var direct) && direct.ValueKind == JsonValueKind.Object)
            {
                return direct;
            }
        }

        foreach (var propertyName in new[] { "data", "item", "result", "value" })
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var nestedContainer) ||
                nestedContainer.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var nestedName in new[] { "standardAttributes", "attributes", "profile", "containerInfo", "container" })
            {
                if (TryGetPropertyIgnoreCase(nestedContainer, nestedName, out var nested) &&
                    nested.ValueKind == JsonValueKind.Object)
                {
                    return nested;
                }
            }

            return nestedContainer;
        }

        return element;
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
            var parsed = await HydrateWorkspaceListItemNamesAsync(
                ParseWorkspaceListItems(document.RootElement, parseFavoriteShape),
                cancellationToken);
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
            var parsed = await HydrateWorkspaceListItemNamesAsync(
                ParseWorkspaceListItems(document.RootElement, parseFavoriteShape),
                cancellationToken);
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
            var parsed = await HydrateWorkspaceListItemNamesAsync(
                ParseWorkspaceListItems(document.RootElement, parseFavoriteShape),
                cancellationToken);
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
                         "rows", "data", "items", "results", "value"
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
                var parsed = await HydrateWorkspaceListItemNamesAsync(
                    ParseWorkspaceListItemsFromV2(document.RootElement),
                    cancellationToken);
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

    private async Task<IReadOnlyList<NdTargetRecentItem>> HydrateWorkspaceListItemNamesAsync(
        IReadOnlyList<NdTargetRecentItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return items;
        }

        await HydrateTargetSelectionNamesAsync(items.Select(item => item.Selection).ToList(), cancellationToken);

        return items;
    }

    private static bool ShouldHydrateSelectionName(NdTargetSelection selection)
    {
        if (string.IsNullOrWhiteSpace(selection.Name))
        {
            return true;
        }

        if (string.Equals(selection.Name, selection.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IsValidContainerDisplayName(selection.Name))
        {
            return true;
        }

        return LooksLikeContainerIdentifier(selection.Name);
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

    private static IEnumerable<string> BuildChildrenEndpointCandidates(
        string cabinetId,
        string? parentContainerId,
        string? workspaceId,
        NdTargetType? preferredType)
    {
        if (!string.IsNullOrWhiteSpace(parentContainerId))
        {
            var escapedParent = Uri.EscapeDataString(parentContainerId);
            var idHint = InferTargetTypeFromContainerId(parentContainerId);
            var effectiveType = preferredType ?? idHint;

            yield return $"/v1/Container/{escapedParent}/children";
            yield return $"/v1/Containers/{escapedParent}/children";

            if (effectiveType is NdTargetType.Folder or NdTargetType.WorkspaceFilter || effectiveType is null)
            {
                yield return $"/v1/Folder/{escapedParent}/children";
                yield return $"/v1/Folder/{escapedParent}";
            }

            if (effectiveType == NdTargetType.Workspace || effectiveType is null)
            {
                if (!string.IsNullOrWhiteSpace(workspaceId))
                {
                    var escapedWorkspace = Uri.EscapeDataString(workspaceId);
                    yield return $"/v1/Workspace/{escapedWorkspace}/children";
                    yield return $"/v1/Workspace/{escapedWorkspace}";
                }
                else
                {
                    // Some tenants expose workspace rows by the same id format; try workspace endpoints as fallback.
                    yield return $"/v1/Workspace/{escapedParent}/children";
                    yield return $"/v1/Workspace/{escapedParent}";
                }
            }

            yield break;
        }

        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            var escapedWorkspaceId = Uri.EscapeDataString(workspaceId);
            yield return $"/v1/Workspace/{escapedWorkspaceId}/children";
            yield return $"/v1/Workspace/{escapedWorkspaceId}";
            yield break;
        }
    }

    private static NdTargetType? InferTargetTypeFromContainerId(string? containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return null;
        }

        var value = Uri.UnescapeDataString(containerId).ToUpperInvariant();
        if (value.Contains(":^F", StringComparison.Ordinal) || value.Contains("^F", StringComparison.Ordinal))
        {
            return NdTargetType.Folder;
        }

        if (value.Contains(":^W", StringComparison.Ordinal) || value.Contains("^W", StringComparison.Ordinal))
        {
            return NdTargetType.Workspace;
        }

        if (value.Contains(":^C", StringComparison.Ordinal) || value.Contains("^C", StringComparison.Ordinal))
        {
            return NdTargetType.Folder;
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateContainerChildrenItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[]
                     {
                         "children", "list", "items", "results", "data", "value", "folders", "records",
                         "standardList", "customList", "locations", "rows"
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
                    var nested = EnumerateContainerChildrenItems(child).ToList();
                    if (nested.Count > 0)
                    {
                        return nested;
                    }
                }
            }
        }

        return Array.Empty<JsonElement>();
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
        var source = SelectTargetSelectionSource(element);
        var id = ResolveContainerIdentifier(element, source);

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var extension = ReadExtensionValue(source);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ReadExtensionValue(element);
        }

        var rawType = string.IsNullOrWhiteSpace(extension)
            ? ReadString(source, "type", "containerType", "kind", "extension", "ext")
            : extension;
        if (string.IsNullOrWhiteSpace(rawType))
        {
            rawType = ReadString(element, "type", "containerType", "kind", "extension", "ext");
        }

        var explicitContainerIdentifier = IsExplicitContainerIdentifier(id);
        var hasContainerTypeHint = IsContainerSelectionTypeToken(rawType) ||
                                   IsContainerSelectionTypeToken(extension) ||
                                   InferTargetTypeFromContainerId(id).HasValue;
        if ((!explicitContainerIdentifier && HasDocumentIdentifierHint(element, source, id) && !hasContainerTypeHint) ||
            IsDocumentLikeType(rawType) ||
            IsDocumentLikeType(extension))
        {
            return null;
        }

        var ambiguousNevIdentifier = IsAmbiguousNevEnvelopeIdentifier(id);
        var hasWorkspaceIdHint =
            TryGetPropertyIgnoreCase(source, "workspaceId", out _) ||
            TryGetPropertyIgnoreCase(element, "workspaceId", out _);
        var supportedType = NdTargetBrowserLogic.NormalizeSupportedType(rawType, hasWorkspaceIdHint);
        if (supportedType == NdTargetType.Folder &&
            ambiguousNevIdentifier &&
            string.IsNullOrWhiteSpace(rawType) &&
            string.IsNullOrWhiteSpace(extension))
        {
            supportedType = null;
        }
        if (supportedType is null)
        {
            supportedType = InferTargetTypeFromContainerId(id);
        }
        var name = ReadPreferredContainerName(element, source);

        var parentId = ReadString(source, "parentId");
        if (string.IsNullOrWhiteSpace(parentId))
        {
            parentId = ReadString(element, "parentId");
        }

        var parentWorkspaceId = ReadString(source, "parentWorkspaceId", "workspaceId", "workspace");
        if (string.IsNullOrWhiteSpace(parentWorkspaceId))
        {
            parentWorkspaceId = ReadString(element, "parentWorkspaceId", "workspaceId", "workspace");
        }

        var pathDisplay = ReadString(source, "path", "fullPath", "breadcrumb");
        if (string.IsNullOrWhiteSpace(pathDisplay))
        {
            pathDisplay = ReadString(element, "path", "fullPath", "breadcrumb");
        }

        return new NdContainerNode
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            TypeRaw = rawType,
            Extension = extension,
            ParentId = parentId,
            ParentWorkspaceId = parentWorkspaceId,
            PathDisplay = pathDisplay,
            SupportedType = supportedType,
            IsSelectable = supportedType.HasValue,
            UnsupportedReason = NdTargetBrowserLogic.GetUnsupportedReason(supportedType),
            HasChildren = ReadBool(source, "hasChildren", "containsChildren", "canExpand") ||
                          ReadBool(element, "hasChildren", "containsChildren", "canExpand"),
            ChildrenLoadState = NdChildrenLoadState.NotLoaded
        };
    }

    private static string ResolveContainerIdentifier(JsonElement element, JsonElement source)
    {
        var id = ReadString(
            element,
            "id",
            "containerId",
            "workspaceId",
            "folderId",
            "envelopeId",
            "envId",
            "environmentId");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = ReadString(
                source,
                "id",
                "containerId",
                "workspaceId",
                "folderId",
                "envelopeId",
                "envId",
                "environmentId");
        }

        var envId = ReadString(source, "envId", "environmentId", "env", "environment");
        if (string.IsNullOrWhiteSpace(envId))
        {
            envId = ReadString(element, "envId", "environmentId", "env", "environment");
        }

        // Some list rows use numeric ids while also returning envId.
        // Prefer explicit env-form ids so downstream parsing/classification stays container-accurate.
        if (!string.IsNullOrWhiteSpace(envId) &&
            IsExplicitContainerIdentifier(envId))
        {
            return envId;
        }

        return id ?? string.Empty;
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
            candidates.Add($"/v2/search?cabinets={escapedCabinet}&container={escapedContainer}&top={top}&filter={filter}&filtertype=IncludeOnly&listflags=FoldersOnly,ValidateWorkspaces");
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
                    var parsed = ParseTargetSelection(item, extension);
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
            foreach (var name in new[]
                     {
                         "items", "results", "data", "documents", "records", "value", "list",
                         "standardList", "customList", "locations", "rows", "searchResults", "hits"
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
                    var nested = EnumerateSearchItems(child).ToList();
                    if (nested.Count > 0)
                    {
                        return nested;
                    }
                }
            }
        }

        return Array.Empty<JsonElement>();
    }

    private static bool IsExtensionMatch(JsonElement element, string extension)
    {
        var ext = ReadExtensionValue(element);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ReadString(element, "type", "containerType", "kind");
        }

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

    private static IEnumerable<string> BuildCabinetTopLevelFoldersEndpointCandidates(string cabinetId)
    {
        var escaped = Uri.EscapeDataString(cabinetId);
        yield return $"/v2/cabinet/{escaped}/folders?top=200&listflags=FoldersOnly,ValidateWorkspaces";
        yield return $"/v2/cabinet/{escaped}/folders?top=200&listflags=FoldersOnly";
        yield return $"/v1/Cabinet/{escaped}/folders";
    }

    private static void AddSearchScopeCandidate(ICollection<string> candidates, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!candidates.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(value);
        }
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
        foreach (var candidate in ExpandContainerIdVariants(colonId))
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
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

        foreach (var candidate in ExpandContainerIdVariants(Uri.UnescapeDataString(normalized)))
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> ExpandContainerIdVariants(string? containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            yield break;
        }

        var trimmed = containerId.Trim();
        var unversioned = TrimContainerIdVersionSuffix(trimmed);
        if (!string.IsNullOrWhiteSpace(unversioned))
        {
            yield return unversioned;
        }

        yield return trimmed;

        if (LooksLikeStructuredContainerIdentifier(trimmed))
        {
            var withNev = EnsureNevSuffixBeforeVersion(trimmed);
            if (!string.Equals(withNev, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                var withNevUnversioned = TrimContainerIdVersionSuffix(withNev);
                if (!string.IsNullOrWhiteSpace(withNevUnversioned))
                {
                    yield return withNevUnversioned;
                }

                yield return withNev;
            }

            yield break;
        }

        if (!trimmed.StartsWith("^", StringComparison.Ordinal) &&
            LooksLikeLegacyEnvToken(trimmed))
        {
            yield return $"^{trimmed}";
        }

        if (trimmed.IndexOf(".nev", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            yield break;
        }

        if (LooksLikeLegacyEnvToken(trimmed))
        {
            yield return $"{trimmed}.nev";
            yield return $"^{trimmed}.nev";
        }
    }

    private static bool LooksLikeStructuredContainerIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith(":", StringComparison.Ordinal) &&
            trimmed.Count(ch => ch == ':') >= 3)
        {
            return true;
        }

        return trimmed.IndexOf(':') >= 0 &&
               trimmed.IndexOf(".nev", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool LooksLikeLegacyEnvToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("^", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.Length < 2)
        {
            return false;
        }

        var prefix = char.ToUpperInvariant(trimmed[0]);
        if (prefix is not ('F' or 'C' or 'W'))
        {
            return false;
        }

        return trimmed[1..].All(char.IsDigit);
    }

    private static string EnsureNevSuffixBeforeVersion(string containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return containerId;
        }

        var trimmed = containerId.Trim();
        var pipeIndex = trimmed.IndexOf('|');
        var basePart = pipeIndex >= 0 ? trimmed[..pipeIndex] : trimmed;
        var suffix = pipeIndex >= 0 ? trimmed[pipeIndex..] : string.Empty;
        if (basePart.EndsWith(".nev", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{basePart}.nev{suffix}";
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

    private static bool HasDocumentIdentifierHint(
        JsonElement element,
        JsonElement source,
        string? resolvedContainerId = null)
    {
        return HasDocumentIdentifierHint(source, resolvedContainerId) ||
               HasDocumentIdentifierHint(element, resolvedContainerId);
    }

    private static bool HasDocumentIdentifierHint(JsonElement node, string? resolvedContainerId)
    {
        var documentId = ReadString(node, "docId", "documentId");
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(resolvedContainerId) &&
            string.Equals(
                documentId.Trim(),
                resolvedContainerId.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsDocumentLikeType(string? rawType)
    {
        var normalized = (rawType ?? string.Empty)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized is "doc" or "document" or "docs" or "nddoc" or "file" or "email" or "eml" or "msg";
    }

    private static bool IsAmbiguousNevEnvelopeIdentifier(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var value = Uri.UnescapeDataString(id).ToUpperInvariant();
        if (!value.Contains(".NEV", StringComparison.Ordinal))
        {
            return false;
        }

        if (value.Contains("^F", StringComparison.Ordinal) ||
            value.Contains("^C", StringComparison.Ordinal) ||
            value.Contains("^W", StringComparison.Ordinal))
        {
            return false;
        }

        return value.Contains(":~", StringComparison.Ordinal);
    }

    private static bool IsExplicitContainerIdentifier(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var value = Uri.UnescapeDataString(id).ToUpperInvariant();
        return value.Contains("^W", StringComparison.Ordinal) ||
               value.Contains("^F", StringComparison.Ordinal) ||
               value.Contains("^C", StringComparison.Ordinal);
    }

    private static bool IsContainerSelectionTypeToken(string? rawType)
    {
        var normalized = (rawType ?? string.Empty)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
        return normalized is "workspace" or "workspacefilter" or "folder" or "ndws" or "ndflt" or "ndfld";
    }

    private static bool IsWorkspaceTypeToken(string? rawType)
    {
        var normalized = (rawType ?? string.Empty)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
        return normalized is "workspace" or "ndws";
    }

    private static bool IsLikelyDocumentDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length > 320)
        {
            candidate = candidate[..320];
        }

        var dotIndex = candidate.LastIndexOf('.');
        if (dotIndex <= 0 || dotIndex >= candidate.Length - 1)
        {
            return false;
        }

        var extension = candidate[(dotIndex + 1)..].Trim().ToLowerInvariant();
        return extension is "msg" or "eml" or "pdf" or "doc" or "docx" or "dotx" or "rtf" or "txt" or
               "xls" or "xlsx" or "ppt" or "pptx" or "csv" or "zip" or "jpg" or "jpeg" or "png" or "tif" or "tiff";
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
            foreach (var propertyName in new[]
                     {
                         "standardAttributes", "attributes", "profile", "containerInfo", "container",
                         "data", "item", "result", "value"
                     })
            {
                if (TryGetPropertyIgnoreCase(element, propertyName, out var node) && node.ValueKind == JsonValueKind.Object)
                {
                    extension = ReadString(node, "extension", "ext", "Ext", "type", "Type");
                    if (!string.IsNullOrWhiteSpace(extension))
                    {
                        return extension;
                    }

                    foreach (var nestedName in new[] { "standardAttributes", "attributes", "profile", "containerInfo", "container" })
                    {
                        if (!TryGetPropertyIgnoreCase(node, nestedName, out var nestedNode) ||
                            nestedNode.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        extension = ReadString(nestedNode, "extension", "ext", "Ext", "type", "Type");
                        if (!string.IsNullOrWhiteSpace(extension))
                        {
                            return extension;
                        }
                    }
                }
            }
        }

        return string.Empty;
    }

    private async Task<TargetDefaultsResolutionResult> TryFetchTargetDefaultsAsync(
        NdTargetSelection target,
        IReadOnlyList<NdProfileAttribute> attributes,
        WorkspaceLookupContext? lookupContext,
        CancellationToken cancellationToken)
    {
        if (!_defaultsEndpointFamilyUnavailableForSession)
        {
            var allClientErrors = true;
            var attemptedCount = 0;
            foreach (var path in BuildDefaultEndpointCandidates(target))
            {
                attemptedCount++;
                try
                {
                    using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                    allClientErrors = false;
                    var parsed = ParseEffectiveDefaults(document.RootElement, attributes);
                    if (parsed.HasValues)
                    {
                        return new TargetDefaultsResolutionResult(parsed, TargetDefaultsSource.V1Endpoints);
                    }
                }
                catch (Exception ex)
                {
                    allClientErrors &= IsClientError400Or404(ex);
                }
            }

            if (attemptedCount > 0 && allClientErrors)
            {
                _defaultsEndpointFamilyUnavailableForSession = true;
                _defaultsEndpointFamilySkipLogged = true;
                Trace.WriteLine("ND-PROFILE defaults-endpoint-family unavailable; using fallback path");
            }
        }
        else if (!_defaultsEndpointFamilySkipLogged)
        {
            _defaultsEndpointFamilySkipLogged = true;
            Trace.WriteLine("ND-PROFILE defaults-endpoint-family unavailable; using fallback path");
        }

        var fromLookupContext = BuildDefaultsFromWorkspaceLookupContext(attributes, lookupContext);
        if (fromLookupContext.HasValues)
        {
            return new TargetDefaultsResolutionResult(fromLookupContext, TargetDefaultsSource.WorkspaceLookupContext);
        }

        var fromContainerInfo = await TryFetchDefaultsFromV2ContainerInfoAsync(target, attributes, cancellationToken);
        if (fromContainerInfo.HasValues)
        {
            return new TargetDefaultsResolutionResult(fromContainerInfo, TargetDefaultsSource.V2ContainerInfo);
        }

        return new TargetDefaultsResolutionResult(EffectiveProfileDefaults.Empty, TargetDefaultsSource.None);
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

    private static bool IsClientError400Or404(Exception ex)
    {
        if (ex is not InvalidOperationException invalidOperation)
        {
            return false;
        }

        return invalidOperation.Message.Contains("(400 ", StringComparison.OrdinalIgnoreCase) ||
               invalidOperation.Message.Contains("(404 ", StringComparison.OrdinalIgnoreCase) ||
               invalidOperation.Message.Contains(" 400 ", StringComparison.OrdinalIgnoreCase) ||
               invalidOperation.Message.Contains(" 404 ", StringComparison.OrdinalIgnoreCase);
    }

    private static EffectiveProfileDefaults BuildDefaultsFromWorkspaceLookupContext(
        IReadOnlyList<NdProfileAttribute> attributes,
        WorkspaceLookupContext? lookupContext)
    {
        if (lookupContext is null)
        {
            return EffectiveProfileDefaults.Empty;
        }

        var defaults = new EffectiveProfileDefaults();
        if (lookupContext.IsParentChild)
        {
            AddDefaultFromAttributeNumber(
                defaults,
                attributes,
                lookupContext.ParentAttrNum,
                lookupContext.ParentKey,
                lookupContext.ParentAttrName);
            AddDefaultFromAttributeNumber(
                defaults,
                attributes,
                lookupContext.ChildAttrNum,
                lookupContext.ChildKey,
                lookupContext.ChildAttrName);
        }
        else
        {
            var attrNum = lookupContext.ParentAttrNum > 0
                ? lookupContext.ParentAttrNum
                : lookupContext.WorkspaceAttrNum;
            var attrName = string.IsNullOrWhiteSpace(lookupContext.ParentAttrName)
                ? lookupContext.WorkspaceAttrName
                : lookupContext.ParentAttrName;
            var key = !string.IsNullOrWhiteSpace(lookupContext.ParentKey)
                ? lookupContext.ParentKey
                : lookupContext.ChildKey;
            AddDefaultFromAttributeNumber(defaults, attributes, attrNum, key, attrName);
        }

        return defaults;
    }

    private async Task<EffectiveProfileDefaults> TryFetchDefaultsFromV2ContainerInfoAsync(
        NdTargetSelection target,
        IReadOnlyList<NdProfileAttribute> attributes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.Id))
        {
            return EffectiveProfileDefaults.Empty;
        }

        var defaults = new EffectiveProfileDefaults();
        var select = Uri.EscapeDataString(
            "StandardAttributes,CustomAttributes,StatusAttributes,ContainerInfo,DeletedStatus,Descriptions,DispNames,UseLongName");
        var candidateIds = BuildContainerIdCandidates(target.Id, NormalizeWorkspaceEnvId(target.Id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        if (candidateIds.Count == 0)
        {
            candidateIds.Add(target.Id);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidateId in candidateIds)
        {
            if (!seen.Add(candidateId))
            {
                continue;
            }

            try
            {
                var encoded = EncodeContainerIdForPath(candidateId);
                var path = $"/v2/container/{encoded}/info?select={select}&options=AddToRecents";
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    TryGetPropertyIgnoreCase(root, "data", out var dataNode) &&
                    dataNode.ValueKind == JsonValueKind.Object)
                {
                    root = dataNode;
                }

                AddDefaultsFromContainerInfoNode(root, attributes, defaults);
                if (defaults.HasValues)
                {
                    return defaults;
                }
            }
            catch
            {
                // Try the next candidate id.
            }
        }

        return defaults;
    }

    private static void AddDefaultsFromContainerInfoNode(
        JsonElement root,
        IReadOnlyList<NdProfileAttribute> attributes,
        EffectiveProfileDefaults defaults)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var propertyName in new[] { "customAttributes", "standardAttributes", "statusAttributes", "attributes" })
        {
            if (TryGetPropertyIgnoreCase(root, propertyName, out var node))
            {
                AddDefaultsFromContainerAttributeNode(node, attributes, defaults);
            }
        }
    }

    private static void AddDefaultsFromContainerAttributeNode(
        JsonElement node,
        IReadOnlyList<NdProfileAttribute> attributes,
        EffectiveProfileDefaults defaults)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                AddDefaultFromContainerAttributeItem(item, attributes, defaults, fallbackAttributeToken: null);
            }

            return;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in node.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                AddDefaultFromContainerAttributeItem(property.Value, attributes, defaults, property.Name);
                continue;
            }

            AddDefaultFromAttributeParts(
                defaults,
                attributes,
                attrNum: null,
                attrId: property.Name,
                attrName: property.Name,
                rawValue: ReadValueAsString(property.Value),
                displayValue: string.Empty);
        }
    }

    private static void AddDefaultFromContainerAttributeItem(
        JsonElement item,
        IReadOnlyList<NdProfileAttribute> attributes,
        EffectiveProfileDefaults defaults,
        string? fallbackAttributeToken)
    {
        if (item.ValueKind == JsonValueKind.Array)
        {
            foreach (var nested in item.EnumerateArray())
            {
                AddDefaultFromContainerAttributeItem(nested, attributes, defaults, fallbackAttributeToken);
            }

            return;
        }

        if (item.ValueKind != JsonValueKind.Object)
        {
            AddDefaultFromAttributeParts(
                defaults,
                attributes,
                attrNum: null,
                attrId: fallbackAttributeToken,
                attrName: fallbackAttributeToken,
                rawValue: ReadValueAsString(item),
                displayValue: string.Empty);
            return;
        }

        var attrNum = ReadNullableInt(item, "attrNum", "attributeNum", "number");
        var attrId = ReadString(item, "attributeId", "attrId", "id", "fieldId");
        if (string.IsNullOrWhiteSpace(attrId))
        {
            attrId = fallbackAttributeToken;
        }

        var attrName = ReadString(item, "attributeName", "field", "name", "label");
        if (string.IsNullOrWhiteSpace(attrName))
        {
            attrName = fallbackAttributeToken;
        }

        var rawValue = ReadString(item, "rawValue", "value", "key", "id");
        if (string.IsNullOrWhiteSpace(rawValue) &&
            TryGetPropertyIgnoreCase(item, "value", out var valueNode))
        {
            rawValue = ReadValueAsString(valueNode);
        }

        var displayValue = ReadString(item, "displayValue", "description", "label", "name");
        if (string.IsNullOrWhiteSpace(displayValue) &&
            TryGetPropertyIgnoreCase(item, "description", out var descriptionNode))
        {
            displayValue = ReadValueAsString(descriptionNode);
        }

        AddDefaultFromAttributeParts(defaults, attributes, attrNum, attrId, attrName, rawValue, displayValue);
    }

    private static void AddDefaultFromAttributeNumber(
        EffectiveProfileDefaults defaults,
        IReadOnlyList<NdProfileAttribute> attributes,
        int attributeNum,
        string? rawValue,
        string? fallbackName)
    {
        if (attributeNum <= 0 || string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        AddDefaultFromAttributeParts(
            defaults,
            attributes,
            attributeNum,
            attrId: attributeNum.ToString(CultureInfo.InvariantCulture),
            attrName: fallbackName,
            rawValue: rawValue,
            displayValue: rawValue);
    }

    private static void AddDefaultFromAttributeParts(
        EffectiveProfileDefaults defaults,
        IReadOnlyList<NdProfileAttribute> attributes,
        int? attrNum,
        string? attrId,
        string? attrName,
        string? rawValue,
        string? displayValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        var attribute = ResolveProfileAttribute(attributes, attrNum, attrId, attrName);
        if (attribute is null)
        {
            return;
        }

        var resolvedId = string.IsNullOrWhiteSpace(attribute.AttributeId)
            ? (attrNum?.ToString(CultureInfo.InvariantCulture) ?? attrId ?? attrName)
            : attribute.AttributeId;
        if (string.IsNullOrWhiteSpace(resolvedId))
        {
            return;
        }

        defaults.ValuesByAttributeId[resolvedId] = new NdProfileValue
        {
            AttributeId = resolvedId,
            AttributeName = string.IsNullOrWhiteSpace(attribute.Name) ? (attrName ?? resolvedId) : attribute.Name,
            RawValue = rawValue.Trim(),
            DisplayValue = string.IsNullOrWhiteSpace(displayValue) ? rawValue.Trim() : displayValue.Trim(),
            PicklistItemId = rawValue.Trim()
        };
    }

    private static NdProfileAttribute? ResolveProfileAttribute(
        IReadOnlyList<NdProfileAttribute> attributes,
        int? attrNum,
        string? attrId,
        string? attrName)
    {
        if (attrNum.HasValue && attrNum.Value > 0)
        {
            var byNum = attributes.FirstOrDefault(a => a.AttributeNum == attrNum.Value);
            if (byNum is not null)
            {
                return byNum;
            }
        }

        if (!string.IsNullOrWhiteSpace(attrId))
        {
            var byId = attributes.FirstOrDefault(a =>
                string.Equals(a.AttributeId, attrId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }

            if (int.TryParse(attrId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAttrNum) &&
                parsedAttrNum > 0)
            {
                var byAttrNum = attributes.FirstOrDefault(a => a.AttributeNum == parsedAttrNum);
                if (byAttrNum is not null)
                {
                    return byAttrNum;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(attrName))
        {
            var byName = attributes.FirstOrDefault(a =>
                string.Equals(a.Name, attrName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }

            if (int.TryParse(attrName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAttrNum) &&
                parsedAttrNum > 0)
            {
                var byAttrNum = attributes.FirstOrDefault(a => a.AttributeNum == parsedAttrNum);
                if (byAttrNum is not null)
                {
                    return byAttrNum;
                }
            }
        }

        if (attrNum.HasValue && attrNum.Value > 0)
        {
            var attrNumText = attrNum.Value.ToString(CultureInfo.InvariantCulture);
            return attributes.FirstOrDefault(a =>
                string.Equals(a.AttributeId, attrNumText, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static string ReadValueAsString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object => ReadString(value, "rawValue", "value", "key", "id", "name", "description"),
            _ => string.Empty
        };
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

