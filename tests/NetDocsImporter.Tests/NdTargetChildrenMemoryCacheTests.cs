using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public class NdTargetChildrenMemoryCacheTests
{
    [Fact]
    public async Task ExpandWorkspace_UsesSingleApiCallWithinTtl_AndMapsFolderFilterTypes()
    {
        var now = new DateTime(2026, 2, 11, 20, 0, 0, DateTimeKind.Utc);
        var cache = new NdTargetChildrenMemoryCache(TimeSpan.FromMinutes(10), () => now);
        var apiCalls = 0;

        async Task<IReadOnlyList<NdContainerNode>> LoadChildren(CancellationToken _)
        {
            apiCalls++;
            await Task.Yield();
            return new[]
            {
                new NdContainerNode
                {
                    Id = "ndfld!10",
                    Name = "General",
                    TypeRaw = "ndfld",
                    Extension = "ndfld",
                    SupportedType = NdTargetBrowserLogic.NormalizeSupportedType("ndfld", hasWorkspaceIdHint: false),
                    IsSelectable = true
                },
                new NdContainerNode
                {
                    Id = "ndflt!11",
                    Name = "My Filter",
                    TypeRaw = "ndflt",
                    Extension = "ndflt",
                    SupportedType = NdTargetBrowserLogic.NormalizeSupportedType("ndflt", hasWorkspaceIdHint: false),
                    IsSelectable = true
                }
            };
        }

        var first = await cache.GetOrLoadAsync("svc", "repo", "cab", "ndws!1", LoadChildren);
        var second = await cache.GetOrLoadAsync("svc", "repo", "cab", "ndws!1", LoadChildren);

        Assert.Equal(1, apiCalls);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(NdTargetType.Folder, first.Single(n => n.Id == "ndfld!10").SupportedType);
        Assert.Equal(NdTargetType.WorkspaceFilter, first.Single(n => n.Id == "ndflt!11").SupportedType);
    }

    [Fact]
    public async Task ExpandWorkspace_AfterTtl_UsesApiAgain()
    {
        var now = new DateTime(2026, 2, 11, 20, 0, 0, DateTimeKind.Utc);
        var cache = new NdTargetChildrenMemoryCache(TimeSpan.FromMinutes(10), () => now);
        var apiCalls = 0;

        Task<IReadOnlyList<NdContainerNode>> LoadChildren(CancellationToken _)
        {
            apiCalls++;
            return Task.FromResult<IReadOnlyList<NdContainerNode>>(new[]
            {
                new NdContainerNode
                {
                    Id = "ndfld!20",
                    Name = "Child",
                    SupportedType = NdTargetType.Folder,
                    IsSelectable = true
                }
            });
        }

        await cache.GetOrLoadAsync("svc", "repo", "cab", "ndws!1", LoadChildren);
        now = now.AddMinutes(11);
        await cache.GetOrLoadAsync("svc", "repo", "cab", "ndws!1", LoadChildren);

        Assert.Equal(2, apiCalls);
    }

    [Fact]
    public async Task ExpandWorkspace_AfterInvalidation_UsesApiAgain()
    {
        var cache = new NdTargetChildrenMemoryCache(TimeSpan.FromMinutes(10), () => DateTime.UtcNow);
        var apiCalls = 0;

        Task<IReadOnlyList<NdContainerNode>> LoadChildren(CancellationToken _)
        {
            apiCalls++;
            return Task.FromResult<IReadOnlyList<NdContainerNode>>(new[]
            {
                new NdContainerNode
                {
                    Id = "ndfld!30",
                    Name = "Child",
                    SupportedType = NdTargetType.Folder,
                    IsSelectable = true
                }
            });
        }

        await cache.GetOrLoadAsync("svc", "repo", "cab", "ndws!1", LoadChildren);
        cache.InvalidateAll();
        await cache.GetOrLoadAsync("svc", "repo", "cab", "ndws!1", LoadChildren);

        Assert.Equal(2, apiCalls);
    }
}
