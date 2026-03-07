using NetDocsImporter.NetDocs;

namespace NetDocsImporter.Tests;

public sealed class NdExportScopePreferenceTests
{
    [Fact]
    public void IsPreferredCanonicalScope_PrefersFolderOverWorkspaceFilter()
    {
        var folder = new NdExportScope
        {
            Kind = NdExportScopeKind.Folder,
            PathSegments = new List<string> { "Matter", "000" }
        };
        var filter = new NdExportScope
        {
            Kind = NdExportScopeKind.WorkspaceFilter,
            PathSegments = new List<string> { "Matter", "All docs" }
        };

        Assert.True(NdExportScopePreference.IsPreferredCanonicalScope(folder, filter));
        Assert.False(NdExportScopePreference.IsPreferredCanonicalScope(filter, folder));
    }

    [Fact]
    public void IsPreferredCanonicalScope_PrefersDeeperFolderPath()
    {
        var shallower = new NdExportScope
        {
            Kind = NdExportScopeKind.Folder,
            PathSegments = new List<string> { "Matter" }
        };
        var deeper = new NdExportScope
        {
            Kind = NdExportScopeKind.Folder,
            PathSegments = new List<string> { "Matter", "Subfolder" }
        };

        Assert.True(NdExportScopePreference.IsPreferredCanonicalScope(deeper, shallower));
        Assert.False(NdExportScopePreference.IsPreferredCanonicalScope(shallower, deeper));
    }

    [Fact]
    public void IsPreferredCanonicalScope_PrefersWorkspaceOverSavedSearch()
    {
        var workspace = new NdExportScope
        {
            Kind = NdExportScopeKind.Workspace,
            PathSegments = new List<string> { "Matter" }
        };
        var savedSearch = new NdExportScope
        {
            Kind = NdExportScopeKind.SavedSearch,
            PathSegments = new List<string> { "Matter", "Recent" }
        };

        Assert.True(NdExportScopePreference.IsPreferredCanonicalScope(workspace, savedSearch));
        Assert.False(NdExportScopePreference.IsPreferredCanonicalScope(savedSearch, workspace));
    }
}
