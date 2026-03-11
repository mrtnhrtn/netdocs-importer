using NetDocsImporter.Core;
using NetDocsImporter.Data;

namespace NetDocsImporter.NetDocs;

/// <summary>
/// Provides read-only access to locally synced NetDocuments metadata stored in the application database.
/// </summary>
public sealed class NetDocumentsMetadataProvider : INetDocumentsMetadataProvider
{
    private readonly JobStore _jobStore;

    /// <summary>
    /// Initializes a metadata provider over the shared job store.
    /// </summary>
    /// <param name="jobStore">Database-backed job store containing synced metadata tables.</param>
    public NetDocumentsMetadataProvider(JobStore jobStore)
    {
        _jobStore = jobStore;
    }

    /// <summary>
    /// Retrieves synced cabinet attributes used for profile validation and mapping.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier.</param>
    /// <param name="cancellationToken">Token used to cancel database work.</param>
    /// <returns>Synced attribute rows for the cabinet.</returns>
    public async Task<IReadOnlyList<NetDocumentsSyncedAttribute>> GetSyncedAttributesAsync(
        string cabinetId,
        CancellationToken cancellationToken = default)
    {
        await _jobStore.InitializeAsync(cancellationToken);
        var attributes = await _jobStore.GetNetDocumentsAttributesAsync(cabinetId, cancellationToken);
        return attributes
            .Select(a => new NetDocumentsSyncedAttribute(
                a.CabinetId,
                a.RepositoryId,
                a.AttributeNum,
                a.Name,
                a.DataType,
                a.IsLookup,
                a.IsMultiValue,
                a.ParentAttributeNum))
            .ToList();
    }

    /// <summary>
    /// Retrieves synced lookup values for an attribute, optionally filtered by parent key.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier.</param>
    /// <param name="attributeNum">Attribute number.</param>
    /// <param name="parentKey">Optional parent key for parent-child lookup tables.</param>
    /// <param name="cancellationToken">Token used to cancel database work.</param>
    /// <returns>Lookup values matching the requested scope.</returns>
    public async Task<IReadOnlyList<NetDocumentsSyncedLookupValue>> GetLookupValuesAsync(
        string cabinetId,
        int attributeNum,
        string? parentKey = null,
        CancellationToken cancellationToken = default)
    {
        await _jobStore.InitializeAsync(cancellationToken);
        var values = await _jobStore.GetNetDocumentsLookupValuesAsync(cabinetId, attributeNum, parentKey, cancellationToken);
        return values
            .Select(v => new NetDocumentsSyncedLookupValue(
                v.CabinetId,
                v.AttributeNum,
                v.ParentKey,
                v.ValueKey,
                v.Description))
            .ToList();
    }
}
