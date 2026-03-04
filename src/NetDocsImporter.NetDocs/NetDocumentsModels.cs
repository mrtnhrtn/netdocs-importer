namespace NetDocsImporter.NetDocs;

/// <summary>
/// Represents the authenticated NetDocuments user identity returned by profile endpoints.
/// </summary>
public sealed record NetDocumentsUserInfo(string UserId, string DisplayName, string Email);
