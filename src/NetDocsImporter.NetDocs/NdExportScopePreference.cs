namespace NetDocsImporter.NetDocs;

public static class NdExportScopePreference
{
    public static bool IsPreferredCanonicalScope(NdExportScope candidate, NdExportScope current)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(current);

        var candidateRank = GetScopeRank(candidate.Kind);
        var currentRank = GetScopeRank(current.Kind);
        if (candidateRank != currentRank)
        {
            return candidateRank < currentRank;
        }

        var candidateDepth = candidate.PathSegments.Count;
        var currentDepth = current.PathSegments.Count;
        if (candidateDepth != currentDepth)
        {
            return candidateDepth > currentDepth;
        }

        var candidatePath = string.Join('/', candidate.PathSegments);
        var currentPath = string.Join('/', current.PathSegments);
        return string.Compare(candidatePath, currentPath, StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static int GetScopeRank(NdExportScopeKind kind)
    {
        return kind switch
        {
            NdExportScopeKind.Folder => 0,
            NdExportScopeKind.Workspace => 1,
            NdExportScopeKind.Collabspace => 2,
            NdExportScopeKind.SavedSearch => 3,
            NdExportScopeKind.WorkspaceFilter => 4,
            _ => 5
        };
    }
}
