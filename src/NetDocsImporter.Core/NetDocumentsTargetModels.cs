using System.Text.Json;

namespace NetDocsImporter.Core;

public enum NdTargetBrowserTab
{
    Recent,
    Favorites,
    GoToWorkspace,
    Browse
}

public enum NdTargetType
{
    Workspace,
    WorkspaceFilter,
    Folder
}

public enum NdTargetSource
{
    Server,
    Local
}

public enum NdTargetSourceFlow
{
    LookupWs,
    Recent,
    Favorite,
    Browse
}

public enum NdChildrenLoadState
{
    NotLoaded,
    Loading,
    Loaded,
    Failed
}

public sealed class NdTargetSelection
{
    public NdTargetType Type { get; set; }

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? ParentWorkspaceId { get; set; }

    public string Extension { get; set; } = string.Empty;

    public NdTargetSourceFlow SourceFlow { get; set; } = NdTargetSourceFlow.Browse;
}

public sealed class NdTargetRecentItem
{
    public NdTargetSelection Selection { get; set; } = new();

    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

    public NdTargetSource Source { get; set; } = NdTargetSource.Local;
}

public sealed class NdTargetFavoriteItem
{
    public NdTargetSelection Selection { get; set; } = new();

    public DateTime PinnedUtc { get; set; } = DateTime.UtcNow;

    public NdTargetSource Source { get; set; } = NdTargetSource.Local;
}

public sealed class NdWorkspaceSearchResult
{
    public string WorkspaceId { get; set; } = string.Empty;

    public string WorkspaceName { get; set; } = string.Empty;

    public string RepositoryId { get; set; } = string.Empty;

    public string? CabinetId { get; set; }
}

public sealed class NdContainerNode
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string TypeRaw { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public string ParentId { get; set; } = string.Empty;

    public string? ParentWorkspaceId { get; set; }

    public string PathDisplay { get; set; } = string.Empty;

    public NdTargetType? SupportedType { get; set; }

    public bool IsSelectable { get; set; }

    public string UnsupportedReason { get; set; } = string.Empty;

    public bool HasChildren { get; set; }

    public NdChildrenLoadState ChildrenLoadState { get; set; }

    public List<NdContainerNode> Children { get; set; } = new();
}

public sealed class NdLookupValueItem
{
    public string Key { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool Closed { get; set; }

    public string ParentKey { get; set; } = string.Empty;

    public string ParentDescription { get; set; } = string.Empty;
}

public sealed class WorkspaceLookupContext
{
    public string RepositoryId { get; set; } = string.Empty;

    public string CabinetId { get; set; } = string.Empty;

    public bool WorkspaceEnabled { get; set; } = true;

    public int WorkspaceAttrNum { get; set; }

    public string WorkspaceAttrName { get; set; } = string.Empty;

    public bool IsParentChild { get; set; } = true;

    public int ParentAttrNum { get; set; }

    public int ChildAttrNum { get; set; }

    public string ParentAttrName { get; set; } = string.Empty;

    public string ChildAttrName { get; set; } = string.Empty;

    public bool? AllowFileInWorkspaces { get; set; }

    public string ParentKey { get; set; } = string.Empty;

    public string ChildKey { get; set; } = string.Empty;

    public string ToJson()
    {
        return JsonSerializer.Serialize(this);
    }

    public static WorkspaceLookupContext? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkspaceLookupContext>(json);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class NdProfileAttribute
{
    public string AttributeId { get; set; } = string.Empty;

    public int? AttributeNum { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsRequired { get; set; }

    public string DataType { get; set; } = string.Empty;

    public bool IsPicklist { get; set; }
}

public sealed class NdProfileValue
{
    public string AttributeId { get; set; } = string.Empty;

    public string AttributeName { get; set; } = string.Empty;

    public string RawValue { get; set; } = string.Empty;

    public string DisplayValue { get; set; } = string.Empty;

    public string? PicklistItemId { get; set; }
}

public sealed class EffectiveProfileDefaults
{
    public Dictionary<string, NdProfileValue> ValuesByAttributeId { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static EffectiveProfileDefaults Empty { get; } = new();

    public bool HasValues => ValuesByAttributeId.Count > 0;

    public string ToJson()
    {
        return JsonSerializer.Serialize(this);
    }

    public static EffectiveProfileDefaults FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<EffectiveProfileDefaults>(json) ?? Empty;
        }
        catch
        {
            return Empty;
        }
    }
}

public sealed class NdTargetProfileSnapshot
{
    public NdTargetSelection Target { get; set; } = new();

    public IReadOnlyList<NdProfileAttribute> Attributes { get; set; } = Array.Empty<NdProfileAttribute>();

    public EffectiveProfileDefaults EffectiveDefaults { get; set; } = EffectiveProfileDefaults.Empty;

    public DateTime SyncedUtc { get; set; } = DateTime.UtcNow;
}

public static class NdTargetBrowserLogic
{
    private const string UnsupportedMessage = "Only Workspace, Workspace Filter, or Folder are supported as upload destinations in this version.";

    public static string BuildTargetKey(NdTargetSelection selection)
    {
        return $"{selection.Type}:{selection.Id}".Trim();
    }

    public static NdTargetType? NormalizeSupportedType(string? rawType, bool hasWorkspaceIdHint)
    {
        var normalized = (rawType ?? string.Empty)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return hasWorkspaceIdHint ? NdTargetType.Folder : null;
        }

        return normalized.ToLowerInvariant() switch
        {
            "workspace" => NdTargetType.Workspace,
            "workspacefilter" => NdTargetType.WorkspaceFilter,
            "folder" => NdTargetType.Folder,
            "ndws" => NdTargetType.Workspace,
            "ndflt" => NdTargetType.WorkspaceFilter,
            "ndfld" => NdTargetType.Folder,
            _ => null
        };
    }

    public static string GetUnsupportedReason(NdTargetType? supportedType)
    {
        return supportedType.HasValue ? string.Empty : UnsupportedMessage;
    }

    public static IReadOnlyList<NdTargetRecentItem> MergeRecentTargets(
        IReadOnlyList<NdTargetRecentItem> serverItems,
        IReadOnlyList<NdTargetRecentItem> localItems,
        int maxItems = 30)
    {
        var merged = new Dictionary<string, NdTargetRecentItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in serverItems.Concat(localItems))
        {
            var key = BuildTargetKey(item.Selection);
            if (!merged.TryGetValue(key, out var existing) || item.LastUsedUtc > existing.LastUsedUtc)
            {
                merged[key] = item;
            }
        }

        return merged.Values
            .OrderByDescending(i => i.LastUsedUtc)
            .Take(Math.Max(1, maxItems))
            .ToList();
    }

    public static IReadOnlyList<NdTargetFavoriteItem> MergeFavoriteTargets(
        IReadOnlyList<NdTargetFavoriteItem> serverItems,
        IReadOnlyList<NdTargetFavoriteItem> localItems)
    {
        var merged = new Dictionary<string, NdTargetFavoriteItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in serverItems.Concat(localItems))
        {
            var key = BuildTargetKey(item.Selection);
            if (!merged.TryGetValue(key, out var existing) || item.PinnedUtc > existing.PinnedUtc)
            {
                merged[key] = item;
            }
        }

        return merged.Values
            .OrderBy(i => i.Selection.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string SerializeRecentTargets(IEnumerable<NdTargetRecentItem> items)
    {
        return JsonSerializer.Serialize(items);
    }

    public static string SerializeFavoriteTargets(IEnumerable<NdTargetFavoriteItem> items)
    {
        return JsonSerializer.Serialize(items);
    }

    public static IReadOnlyList<NdTargetRecentItem> DeserializeRecentTargets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<NdTargetRecentItem>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<NdTargetRecentItem>>(json) ?? new List<NdTargetRecentItem>();
        }
        catch
        {
            return Array.Empty<NdTargetRecentItem>();
        }
    }

    public static IReadOnlyList<NdTargetFavoriteItem> DeserializeFavoriteTargets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<NdTargetFavoriteItem>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<NdTargetFavoriteItem>>(json) ?? new List<NdTargetFavoriteItem>();
        }
        catch
        {
            return Array.Empty<NdTargetFavoriteItem>();
        }
    }
}
