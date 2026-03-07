using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public class UploadQueueContextValidatorTests
{
    [Fact]
    public void TryValidate_ReturnsTrue_WhenSnapshotMatchesCurrentContext()
    {
        var snapshot = CreateSnapshot(
            repositoryId: "repo-a",
            cabinetId: "cab-a",
            apiBaseUrl: "https://api.vault.netvoyage.com/");

        var valid = UploadQueueContextValidator.TryValidate(
            snapshot,
            currentRepositoryId: "repo-a",
            currentCabinetId: "cab-a",
            currentApiBaseUrl: "https://api.vault.netvoyage.com",
            out var error);

        Assert.True(valid);
        Assert.True(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryValidate_ReturnsFalse_WhenRepositoryDiffers()
    {
        var snapshot = CreateSnapshot(
            repositoryId: "repo-a",
            cabinetId: "cab-a",
            apiBaseUrl: "https://api.vault.netvoyage.com");

        var valid = UploadQueueContextValidator.TryValidate(
            snapshot,
            currentRepositoryId: "repo-b",
            currentCabinetId: "cab-a",
            currentApiBaseUrl: "https://api.vault.netvoyage.com",
            out var error);

        Assert.False(valid);
        Assert.Contains("repository", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_ReturnsFalse_WhenApiBaseDiffers()
    {
        var snapshot = CreateSnapshot(
            repositoryId: "repo-a",
            cabinetId: "cab-a",
            apiBaseUrl: "https://api.eu.netdocuments.com");

        var valid = UploadQueueContextValidator.TryValidate(
            snapshot,
            currentRepositoryId: "repo-a",
            currentCabinetId: "cab-a",
            currentApiBaseUrl: "https://api.vault.netvoyage.com",
            out var error);

        Assert.False(valid);
        Assert.Contains("API base", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_ReturnsFalse_WhenSnapshotPlanContextMisaligned()
    {
        var snapshot = new UploadQueueSnapshot
        {
            RepositoryId = "repo-a",
            CabinetId = "cab-a",
            PlanContext = new DirectUploadPlanContext
            {
                RepositoryId = "repo-a",
                CabinetId = "cab-b",
                ApiBaseUrl = "https://api.vault.netvoyage.com"
            }
        };

        var valid = UploadQueueContextValidator.TryValidate(
            snapshot,
            currentRepositoryId: "repo-a",
            currentCabinetId: "cab-a",
            currentApiBaseUrl: "https://api.vault.netvoyage.com",
            out var error);

        Assert.False(valid);
        Assert.Contains("snapshot context cabinet", error, StringComparison.OrdinalIgnoreCase);
    }

    private static UploadQueueSnapshot CreateSnapshot(string repositoryId, string cabinetId, string apiBaseUrl)
    {
        return new UploadQueueSnapshot
        {
            RepositoryId = repositoryId,
            CabinetId = cabinetId,
            PlanContext = new DirectUploadPlanContext
            {
                RepositoryId = repositoryId,
                CabinetId = cabinetId,
                ApiBaseUrl = apiBaseUrl
            }
        };
    }
}
