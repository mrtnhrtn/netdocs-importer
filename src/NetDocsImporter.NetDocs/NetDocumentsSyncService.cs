using System.Text.Json;
using NetDocsImporter.Data;

namespace NetDocsImporter.NetDocs;

public sealed partial class NetDocumentsSyncService
{
    private readonly NetDocumentsApiClient _apiClient;
    private readonly JobStore _jobStore;

    public NetDocumentsSyncService(NetDocumentsApiClient apiClient, JobStore jobStore)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
    }

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

            if (string.IsNullOrWhiteSpace(repositoryId))
            {
                using var cabinetInfo = await _apiClient.GetJsonAsync($"/v1/Cabinet/{Uri.EscapeDataString(id)}/info", cancellationToken);
                var infoRoot = UnwrapSingle(cabinetInfo.RootElement);
                repositoryId = ReadString(infoRoot, "repositoryId", "repoId");
                repositoryName = ReadString(infoRoot, "repositoryName", "repoName");
            }

            cabinets.Add(new NetDocumentsCabinetRecord(
                id,
                repositoryId,
                repositoryName,
                name,
                ReadString(cabinetElement, "description"),
                region,
                DateTime.UtcNow));
        }

        await _jobStore.InitializeAsync(cancellationToken);
        await _jobStore.ReplaceNetDocumentsCabinetsAsync(region, cabinets, cancellationToken);
        return cabinets;
    }

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

            var parentAttrNum = ReadNullableInt(element, "parentAttrNum", "parentAttributeNum", "parentNum");
            var isLookup = ReadBool(element, "isLookup", "lookup", "hasLookup");
            var isMultiValue = ReadBool(element, "isMultiValue", "multiValue", "multivalue");
            var isRequired = ReadBool(element, "isRequired", "required");
            var dataType = ReadString(element, "dataType", "type");
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

    public Task<IReadOnlyList<NetDocumentsAttributeRecord>> GetSyncedAttributesAsync(
        string cabinetId,
        CancellationToken cancellationToken = default)
    {
        return _jobStore.GetNetDocumentsAttributesAsync(cabinetId, cancellationToken);
    }

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
            if (element.TryGetProperty(name, out var value))
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
            if (!element.TryGetProperty(name, out var value))
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
            if (!element.TryGetProperty(name, out var value))
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
                var text = value.GetString();
                if (bool.TryParse(text, out var parsedBool))
                {
                    return parsedBool;
                }

                if (int.TryParse(text, out var parsedInt))
                {
                    return parsedInt != 0;
                }
            }
        }

        return false;
    }
}
