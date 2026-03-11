using System.Diagnostics;
using System.Text.Json;
using NetDocsImporter.Data;

namespace NetDocsImporter.NetDocs;

/// <summary>
/// Synchronizes user, cabinet, attribute, and lookup metadata between NetDocuments APIs and local persistence.
/// </summary>
public sealed partial class NetDocumentsSyncService
{
    private readonly NetDocumentsApiClient _apiClient;
    private readonly JobStore _jobStore;

    /// <summary>
    /// Initializes the synchronization service.
    /// </summary>
    /// <param name="apiClient">Authenticated API client used for NetDocuments calls.</param>
    /// <param name="jobStore">Persistent store used to cache synchronized metadata.</param>
    /// <exception cref="ArgumentNullException">Thrown when a dependency is null.</exception>
    public NetDocumentsSyncService(NetDocumentsApiClient apiClient, JobStore jobStore)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
    }

    public IDisposable PushApiCallTraceObserver(Action<NdApiCallTrace> observer)
    {
        if (observer is null)
        {
            throw new ArgumentNullException(nameof(observer));
        }

        return _apiClient.PushApiCallTraceObserver(observer);
    }

    /// <summary>
    /// Resolves the currently authenticated NetDocuments user.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel API calls.</param>
    /// <returns>User identity details for the current session.</returns>
    /// <exception cref="InvalidOperationException">Thrown when user info cannot be resolved from known endpoints.</exception>
    public async Task<NetDocumentsUserInfo> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var candidates = new[]
        {
            "/v1/User/info",
            "/v1/User/current/info",
            "/v1/User/me/info"
        };

        Exception? last = null;
        foreach (var path in candidates)
        {
            try
            {
                using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
                var root = UnwrapSingle(document.RootElement);

                var userId = ReadString(root, "id", "userId", "userid");
                var displayName = ReadString(root, "displayName", "name", "fullName");
                var email = ReadString(root, "email", "mail");
                if (!string.IsNullOrWhiteSpace(userId) || !string.IsNullOrWhiteSpace(displayName))
                {
                    return new NetDocumentsUserInfo(userId, displayName, email);
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            "Unable to resolve NetDocuments current user info from known endpoints.",
            last);
    }

    /// <summary>
    /// Synchronizes cabinet metadata for a region and stores the results locally.
    /// </summary>
    /// <param name="region">Region key used to scope persisted cabinet rows.</param>
    /// <param name="cancellationToken">Token used to cancel API/database work.</param>
    /// <returns>Synchronized cabinet records.</returns>
    public async Task<IReadOnlyList<NetDocumentsCabinetRecord>> SyncCabinetsAsync(
        string region,
        CancellationToken cancellationToken = default)
    {
        using var document = await _apiClient.GetJsonAsync("/v1/User/cabinets", cancellationToken);
        var cabinetElements = EnumerateArray(document.RootElement).ToList();
        var cabinets = new List<NetDocumentsCabinetRecord>(cabinetElements.Count);

        foreach (var cabinetElement in cabinetElements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = ReadString(cabinetElement, "id", "cabinetId");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = ReadString(cabinetElement, "name", "cabinetName", "description");
            var repositoryId = ReadString(cabinetElement, "repositoryId", "repoId");
            var repositoryName = ReadString(cabinetElement, "repositoryName", "repoName");
            var workspaceAttributeNum = ReadNullableInt(
                cabinetElement,
                "workspaceAttributeId",
                "workspaceAttributeNum",
                "workspaceAttributeNumber",
                "workspaceAttribute");
            var workspacePluralName = ReadString(
                cabinetElement,
                "workspacePluralName",
                "workspaceAttributePluralName",
                "workspacePlural");
            if (!workspaceAttributeNum.HasValue || string.IsNullOrWhiteSpace(workspacePluralName))
            {
                TryReadWorkspaceAttributeFromNode(cabinetElement, ref workspaceAttributeNum, ref workspacePluralName);
            }
            bool? allowFileInWorkspaces = null;

            if (string.IsNullOrWhiteSpace(repositoryId) ||
                !workspaceAttributeNum.HasValue ||
                string.IsNullOrWhiteSpace(workspacePluralName))
            {
                using var cabinetInfo = await _apiClient.GetJsonAsync($"/v1/Cabinet/{Uri.EscapeDataString(id)}/info", cancellationToken);
                var infoRoot = UnwrapSingle(cabinetInfo.RootElement);
                if (string.IsNullOrWhiteSpace(repositoryId))
                {
                    repositoryId = ReadString(infoRoot, "repositoryId", "repoId");
                }

                if (string.IsNullOrWhiteSpace(repositoryName))
                {
                    repositoryName = ReadString(infoRoot, "repositoryName", "repoName");
                }

                workspaceAttributeNum ??= ReadNullableInt(
                    infoRoot,
                    "workspaceAttributeId",
                    "workspaceAttributeNum",
                    "workspaceAttributeNumber",
                    "workspaceAttribute");
                if (string.IsNullOrWhiteSpace(workspacePluralName))
                {
                    workspacePluralName = ReadString(
                        infoRoot,
                        "workspacePluralName",
                        "workspaceAttributePluralName",
                        "workspacePlural");
                }
                if (!workspaceAttributeNum.HasValue || string.IsNullOrWhiteSpace(workspacePluralName))
                {
                    TryReadWorkspaceAttributeFromNode(infoRoot, ref workspaceAttributeNum, ref workspacePluralName);
                }
            }

            try
            {
                using var cabinetSettings = await _apiClient.GetJsonAsync($"/v1/Cabinet/{Uri.EscapeDataString(id)}/settings", cancellationToken);
                var settingsRoot = UnwrapSingle(cabinetSettings.RootElement);
                if (TryGetPropertyIgnoreCase(settingsRoot, "allowFileInWorkspaces", out var allowNode))
                {
                    allowFileInWorkspaces = ReadBool(settingsRoot, "allowFileInWorkspaces");
                }
            }
            catch
            {
                // Settings endpoint can be unavailable in some tenants. Keep syncing cabinets.
            }

            cabinets.Add(new NetDocumentsCabinetRecord(
                id,
                repositoryId,
                repositoryName,
                name,
                ReadString(cabinetElement, "description"),
                workspaceAttributeNum,
                workspacePluralName,
                allowFileInWorkspaces,
                region,
                DateTime.UtcNow));

            Trace.WriteLine(
                $"ND-SYNC cabinet='{id}' repo='{repositoryId}' name='{name}' workspaceAttr='{(workspaceAttributeNum.HasValue ? workspaceAttributeNum.Value.ToString() : "<none>")}' workspacePlural='{workspacePluralName}' allowFileInWorkspaces='{(allowFileInWorkspaces.HasValue ? allowFileInWorkspaces.Value.ToString() : "<unknown>")}'.");
        }

        await _jobStore.InitializeAsync(cancellationToken);
        await _jobStore.ReplaceNetDocumentsCabinetsAsync(region, cabinets, cancellationToken);
        return cabinets;
    }

    /// <summary>
    /// Synchronizes custom attributes (and lookup tables) for a cabinet and stores them locally.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier.</param>
    /// <param name="repositoryId">Owning repository identifier.</param>
    /// <param name="cancellationToken">Token used to cancel API/database work.</param>
    /// <returns>Synchronized attribute records.</returns>
    public async Task<IReadOnlyList<NetDocumentsAttributeRecord>> SyncCabinetAttributesAsync(
        string cabinetId,
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        using var document = await _apiClient.GetJsonAsync($"/v1/Cabinet/{Uri.EscapeDataString(cabinetId)}/customAttributes", cancellationToken);
        var attributeElements = EnumerateArray(document.RootElement).ToList();
        var attributes = new List<NetDocumentsAttributeRecord>(attributeElements.Count);

        foreach (var element in attributeElements)
        {
            var attrNum = ReadInt(element, "attrNum", "attributeNum", "number", "id");
            if (attrNum <= 0)
            {
                continue;
            }

            var parentAttrNum = ReadNullableInt(
                element,
                "parentAttrNum",
                "parentAttributeNum",
                "parentNum",
                "parentAttributeId",
                "parentAttr",
                "parent");
            var isMultiValue = ReadBool(element, "isMultiValue", "multiValue", "multivalue");
            var isRequired = ReadBool(element, "isRequired", "required");
            var dataType = ReadString(element, "dataType", "type", "attributeType", "valueType");
            var isLookup = ReadBool(element, "isLookup", "lookup", "hasLookup", "isPicklist", "picklist");
            if (!isLookup)
            {
                isLookup = LooksLikeLookupAttribute(element, dataType, parentAttrNum);
            }
            var attrId = ReadString(element, "id", "attributeId");
            var name = ReadString(element, "name", "description", "label");

            attributes.Add(new NetDocumentsAttributeRecord(
                cabinetId,
                repositoryId,
                attrNum,
                attrId,
                name,
                dataType,
                isRequired,
                isMultiValue,
                isLookup,
                parentAttrNum,
                parentAttrNum.HasValue,
                DateTime.UtcNow));
        }

        await _jobStore.InitializeAsync(cancellationToken);
        await _jobStore.ReplaceNetDocumentsAttributesAsync(cabinetId, attributes, cancellationToken);

        foreach (var attribute in attributes.Where(a => a.IsLookup))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<NetDocumentsLookupValueRecord> values = attribute.ParentAttributeNum.HasValue
                ? await FetchParentChildLookupValuesAsync(repositoryId, cabinetId, attribute.AttributeNum, cancellationToken)
                : await FetchLookupValuesAsync(repositoryId, cabinetId, attribute.AttributeNum, cancellationToken);

            await _jobStore.ReplaceNetDocumentsLookupValuesAsync(cabinetId, attribute.AttributeNum, values, cancellationToken);
        }

        return attributes;
    }

    /// <summary>
    /// Gets previously synchronized attributes for a cabinet from local storage.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier.</param>
    /// <param name="cancellationToken">Token used to cancel database work.</param>
    /// <returns>Cached attribute records.</returns>
    public Task<IReadOnlyList<NetDocumentsAttributeRecord>> GetSyncedAttributesAsync(
        string cabinetId,
        CancellationToken cancellationToken = default)
    {
        return _jobStore.GetNetDocumentsAttributesAsync(cabinetId, cancellationToken);
    }

    /// <summary>
    /// Gets previously synchronized lookup values for an attribute from local storage.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier.</param>
    /// <param name="attributeNum">Attribute number.</param>
    /// <param name="parentKey">Optional parent key for parent-child lookups.</param>
    /// <param name="cancellationToken">Token used to cancel database work.</param>
    /// <returns>Cached lookup value records.</returns>
    public Task<IReadOnlyList<NetDocumentsLookupValueRecord>> GetLookupValuesAsync(
        string cabinetId,
        int attributeNum,
        string? parentKey = null,
        CancellationToken cancellationToken = default)
    {
        return _jobStore.GetNetDocumentsLookupValuesAsync(cabinetId, attributeNum, parentKey, cancellationToken);
    }

    private async Task<IReadOnlyList<NetDocumentsLookupValueRecord>> FetchLookupValuesAsync(
        string repositoryId,
        string cabinetId,
        int attributeNum,
        CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var skip = 0;
        var values = new List<NetDocumentsLookupValueRecord>();

        while (true)
        {
            var path =
                $"/v1/attributes/{Uri.EscapeDataString(repositoryId)}/{attributeNum}?$select=key,description&$orderby=key&$top={pageSize}&$skip={skip}";
            using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
            var pageItems = EnumerateArray(document.RootElement)
                .Select(item => new NetDocumentsLookupValueRecord(
                    cabinetId,
                    attributeNum,
                    null,
                    ReadString(item, "key", "id"),
                    ReadString(item, "description", "label", "name"),
                    DateTime.UtcNow))
                .Where(value => !string.IsNullOrWhiteSpace(value.ValueKey))
                .ToList();

            if (pageItems.Count == 0)
            {
                break;
            }

            values.AddRange(pageItems);
            if (pageItems.Count < pageSize)
            {
                break;
            }

            skip += pageSize;
        }

        return values;
    }

    private async Task<IReadOnlyList<NetDocumentsLookupValueRecord>> FetchParentChildLookupValuesAsync(
        string repositoryId,
        string cabinetId,
        int childAttributeNum,
        CancellationToken cancellationToken)
    {
        var path = $"/v1/attributes/{Uri.EscapeDataString(repositoryId)}/all/{childAttributeNum}";
        using var document = await _apiClient.GetJsonAsync(path, cancellationToken);
        var values = new List<NetDocumentsLookupValueRecord>();

        foreach (var item in EnumerateArray(document.RootElement))
        {
            var parentKey = ReadString(item, "parentKey", "parent", "parentValueKey");
            var key = ReadString(item, "key", "childKey", "id");
            var description = ReadString(item, "description", "childDescription", "name", "label");
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            values.Add(new NetDocumentsLookupValueRecord(
                cabinetId,
                childAttributeNum,
                parentKey,
                key,
                description,
                DateTime.UtcNow));
        }

        return values;
    }

    private static JsonElement UnwrapSingle(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "data", "item", "result", "value" })
            {
                if (root.TryGetProperty(propertyName, out var child) && child.ValueKind == JsonValueKind.Object)
                {
                    return child;
                }
            }
        }

        return root;
    }

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "data", "items", "results", "value", "cabinets", "attributes" })
            {
                if (root.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Array)
                {
                    return child.EnumerateArray();
                }
            }
        }

        return Array.Empty<JsonElement>();
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? string.Empty;
                }

                if (value.ValueKind == JsonValueKind.Number)
                {
                    return value.GetRawText();
                }
            }
        }

        return string.Empty;
    }

    private static int ReadInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString() ?? string.Empty;
                if (TryExtractFirstInteger(text, out number))
                {
                    return number;
                }
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                number = ReadInt(value, "id", "attrNum", "attributeNum", "number", "value");
                if (number > 0)
                {
                    return number;
                }
            }
        }

        return 0;
    }

    private static int? ReadNullableInt(JsonElement element, params string[] names)
    {
        var value = ReadInt(element, names);
        return value <= 0 ? null : value;
    }

    private static bool ReadBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number != 0;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = (value.GetString() ?? string.Empty).Trim();
                if (bool.TryParse(text, out var parsedBool))
                {
                    return parsedBool;
                }

                if (string.Equals(text, "y", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "t", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(text, "n", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "no", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "f", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (int.TryParse(text, out var parsedInt))
                {
                    return parsedInt != 0;
                }
            }
        }

        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static void TryReadWorkspaceAttributeFromNode(
        JsonElement node,
        ref int? workspaceAttributeNum,
        ref string workspacePluralName)
    {
        foreach (var nestedName in new[] { "standardAttributes", "attributes", "cabinet", "info", "data", "value" })
        {
            if (TryGetPropertyIgnoreCase(node, nestedName, out var nestedNode) && nestedNode.ValueKind == JsonValueKind.Object)
            {
                TryReadWorkspaceAttributeFromNode(nestedNode, ref workspaceAttributeNum, ref workspacePluralName);
            }
        }

        if (TryGetPropertyIgnoreCase(node, "workspaceAttribute", out var workspaceAttributeNode))
        {
            if (!workspaceAttributeNum.HasValue)
            {
                workspaceAttributeNum = ReadNullableInt(
                    workspaceAttributeNode,
                    "id",
                    "attrNum",
                    "attributeNum",
                    "workspaceAttributeId",
                    "number");
            }

            if (string.IsNullOrWhiteSpace(workspacePluralName))
            {
                workspacePluralName = ReadString(
                    workspaceAttributeNode,
                    "pluralName",
                    "workspacePluralName",
                    "name",
                    "description",
                    "label");
            }
        }

        if (TryGetPropertyIgnoreCase(node, "workspace", out var workspaceNode))
        {
            if (!workspaceAttributeNum.HasValue)
            {
                workspaceAttributeNum = ReadNullableInt(
                    workspaceNode,
                    "attributeId",
                    "attributeNum",
                    "workspaceAttributeId",
                    "workspaceAttributeNum");
            }

            if (string.IsNullOrWhiteSpace(workspacePluralName))
            {
                workspacePluralName = ReadString(
                    workspaceNode,
                    "pluralName",
                    "workspacePluralName",
                    "name");
            }
        }
    }

    private static bool LooksLikeLookupAttribute(JsonElement element, string dataType, int? parentAttrNum)
    {
        if (parentAttrNum.HasValue && parentAttrNum.Value > 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(dataType))
        {
            var normalized = dataType.Trim().ToLowerInvariant();
            if (normalized.Contains("lookup", StringComparison.Ordinal) ||
                normalized.Contains("picklist", StringComparison.Ordinal) ||
                normalized.Contains("choice", StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (TryGetPropertyIgnoreCase(element, "lookupTable", out var lookupTable) &&
            lookupTable.ValueKind != JsonValueKind.Null &&
            lookupTable.ValueKind != JsonValueKind.Undefined)
        {
            return true;
        }

        if (TryGetPropertyIgnoreCase(element, "pickList", out var pickList) &&
            pickList.ValueKind == JsonValueKind.Array &&
            pickList.GetArrayLength() > 0)
        {
            return true;
        }

        return false;
    }

    private static bool TryExtractFirstInteger(string text, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var started = false;
        var digits = new List<char>(10);
        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
            {
                started = true;
                digits.Add(ch);
                continue;
            }

            if (started)
            {
                break;
            }
        }

        if (digits.Count == 0)
        {
            return false;
        }

        return int.TryParse(new string(digits.ToArray()), out number);
    }
}
