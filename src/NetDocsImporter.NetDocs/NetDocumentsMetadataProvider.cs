using NetDocsImporter.Core;
using NetDocsImporter.Data;

namespace NetDocsImporter.NetDocs;

public sealed class NetDocumentsMetadataProvider : INetDocumentsMetadataProvider
{
    private readonly JobStore _jobStore;

    public NetDocumentsMetadataProvider(JobStore jobStore)
    {
        _jobStore = jobStore;
    }

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
