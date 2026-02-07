namespace NetDocsImporter.Core;

public sealed record NetDocumentsSyncedAttribute(
    string CabinetId,
    string RepositoryId,
    int AttributeNum,
    string Name,
    string DataType,
    bool IsLookup,
    bool IsMultiValue,
    int? ParentAttributeNum);

public sealed record NetDocumentsSyncedLookupValue(
    string CabinetId,
    int AttributeNum,
    string? ParentKey,
    string Key,
    string Description);

public interface INetDocumentsMetadataProvider
{
    Task<IReadOnlyList<NetDocumentsSyncedAttribute>> GetSyncedAttributesAsync(
        string cabinetId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NetDocumentsSyncedLookupValue>> GetLookupValuesAsync(
        string cabinetId,
        int attributeNum,
        string? parentKey = null,
        CancellationToken cancellationToken = default);
}
