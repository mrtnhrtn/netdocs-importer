using System.Collections.ObjectModel;
using System.Globalization;
using NetDocsImporter.Core;
using NetDocsImporter.Data;

namespace NetDocsImporter.App;

public sealed partial class MainViewModel
{
    private readonly ObservableCollection<ReviewValidationIssueView> _profilingValidationIssues = new();
    private ImportJobContext _jobContext = new();
    private bool _isReviewScopeValidationBusy;
    private int _validationErrorCount;
    private int _validationWarningCount;
    private int _validationInfoCount;

    public ObservableCollection<ReviewValidationIssueView> ProfilingValidationIssues => _profilingValidationIssues;

    public bool IsReviewScopeValidationBusy
    {
        get => _isReviewScopeValidationBusy;
        private set => SetField(ref _isReviewScopeValidationBusy, value);
    }

    public string ReviewTargetRepository => _jobContext.RepositoryId;

    public string ReviewTargetCabinet => _jobContext.CabinetName;

    public string ReviewTargetContainerId => _jobContext.TargetContainerId;

    public string ReviewTargetContainerName => _jobContext.TargetContainerName;

    public string ReviewProfileLastSyncDisplay =>
        _jobContext.NetDocumentsProfileContext?.LastSyncUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "--";

    public string ReviewProfileAttributeCountDisplay =>
        (_jobContext.NetDocumentsProfileContext?.AttributeCount ?? 0).ToString("N0", CultureInfo.CurrentCulture);

    public string ReviewProfileLookupCountDisplay =>
        (_jobContext.NetDocumentsProfileContext?.LookupValueCount ?? 0).ToString("N0", CultureInfo.CurrentCulture);

    public string ReviewProfileRequiredCountDisplay =>
        (_jobContext.NetDocumentsProfileContext?.RequiredAttributeCount ?? 0).ToString("N0", CultureInfo.CurrentCulture);

    public bool ReviewProfileLookupCacheReady => _jobContext.NetDocumentsProfileContext?.HasLookupCache ?? false;

    public string ValidationErrorCountDisplay => _validationErrorCount.ToString("N0", CultureInfo.CurrentCulture);

    public string ValidationWarningCountDisplay => _validationWarningCount.ToString("N0", CultureInfo.CurrentCulture);

    public string ValidationInfoCountDisplay => _validationInfoCount.ToString("N0", CultureInfo.CurrentCulture);

    public bool HasReviewTargetContext =>
        !string.IsNullOrWhiteSpace(_jobContext.RepositoryId) &&
        !string.IsNullOrWhiteSpace(_jobContext.CabinetId);

    public async Task ResyncAttributesForReviewScopeAsync()
    {
        await SyncNetDocumentsAttributesAsync();
        await RefreshReviewScopeNetDocumentsAsync();
    }

    private void InitializeReviewScopeNetDocuments()
    {
        _jobContext = new ImportJobContext
        {
            Region = SelectedNetDocumentsRegion.ToString(),
            RepositoryId = SelectedNetDocumentsRepositoryId,
            CabinetId = SelectedNetDocumentsCabinetId,
            CabinetName = SelectedNetDocumentsCabinetName
        };
    }

    private async Task RefreshReviewScopeNetDocumentsAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentJobId))
        {
            UpdateReviewValidationIssues(Array.Empty<ReviewValidationIssueView>());
            return;
        }

        IsReviewScopeValidationBusy = true;
        try
        {
            UpdateJobContextFromCurrentState();
            _jobContext.NetDocumentsProfileContext = await BuildProfileContextSnapshotAsync();
            NotifyReviewContextChanged();

            var issues = await ValidateProfilingAgainstSyncedMetadataAsync();
            UpdateReviewValidationIssues(issues);
        }
        finally
        {
            IsReviewScopeValidationBusy = false;
        }
    }

    private async Task<NetDocumentsProfileContext?> BuildProfileContextSnapshotAsync()
    {
        if (string.IsNullOrWhiteSpace(_jobContext.CabinetId) || string.IsNullOrWhiteSpace(_jobContext.RepositoryId))
        {
            return null;
        }

        await _jobStore.InitializeAsync();
        var snapshot = await _jobStore.GetNetDocumentsProfileContextSnapshotAsync(
            _jobContext.CabinetId,
            _jobContext.RepositoryId);
        if (snapshot is null)
        {
            return null;
        }

        return new NetDocumentsProfileContext(
            snapshot.CabinetId,
            snapshot.RepositoryId,
            snapshot.AttributeCount,
            snapshot.RequiredAttributeCount,
            snapshot.LookupAttributeCount,
            snapshot.LookupValueCount,
            snapshot.LastSyncedUtc,
            snapshot.LookupAttributeCount == 0 || snapshot.LookupValueCount > 0);
    }

    private void UpdateJobContextFromCurrentState()
    {
        _jobContext.JobId = CurrentJobId ?? string.Empty;
        _jobContext.SourceFolder = SelectedFolder ?? CurrentJobSourceRoot;
        _jobContext.Region = SelectedNetDocumentsRegion.ToString();
        _jobContext.RepositoryId = SelectedNetDocumentsRepositoryId;
        _jobContext.CabinetId = SelectedNetDocumentsCabinetId;
        _jobContext.CabinetName = SelectedNetDocumentsCabinetName;
    }

    private void NotifyReviewContextChanged()
    {
        OnPropertyChanged(nameof(ReviewTargetRepository));
        OnPropertyChanged(nameof(ReviewTargetCabinet));
        OnPropertyChanged(nameof(ReviewTargetContainerId));
        OnPropertyChanged(nameof(ReviewTargetContainerName));
        OnPropertyChanged(nameof(ReviewProfileLastSyncDisplay));
        OnPropertyChanged(nameof(ReviewProfileAttributeCountDisplay));
        OnPropertyChanged(nameof(ReviewProfileLookupCountDisplay));
        OnPropertyChanged(nameof(ReviewProfileRequiredCountDisplay));
        OnPropertyChanged(nameof(ReviewProfileLookupCacheReady));
        OnPropertyChanged(nameof(HasReviewTargetContext));
    }

    private void UpdateReviewValidationIssues(IReadOnlyList<ReviewValidationIssueView> issues)
    {
        UpdateOnUi(() =>
        {
            _profilingValidationIssues.Clear();
            foreach (var issue in issues
                         .OrderBy(i => i.SeverityOrder)
                         .ThenBy(i => i.ScopeOrder)
                         .ThenBy(i => i.Target, StringComparer.OrdinalIgnoreCase))
            {
                _profilingValidationIssues.Add(issue);
            }

            _validationErrorCount = _profilingValidationIssues.Count(i => i.Severity == ReviewValidationSeverity.Error);
            _validationWarningCount = _profilingValidationIssues.Count(i => i.Severity == ReviewValidationSeverity.Warning);
            _validationInfoCount = _profilingValidationIssues.Count(i => i.Severity == ReviewValidationSeverity.Info);

            OnPropertyChanged(nameof(ValidationErrorCountDisplay));
            OnPropertyChanged(nameof(ValidationWarningCountDisplay));
            OnPropertyChanged(nameof(ValidationInfoCountDisplay));
        });
    }

    private async Task<IReadOnlyList<ReviewValidationIssueView>> ValidateProfilingAgainstSyncedMetadataAsync()
    {
        if (_schema is null || string.IsNullOrWhiteSpace(CurrentJobId))
        {
            return Array.Empty<ReviewValidationIssueView>();
        }

        await _jobStore.InitializeAsync();
        var folders = await _jobStore.GetFoldersForJobAsync(CurrentJobId);
        if (folders.Count == 0)
        {
            return Array.Empty<ReviewValidationIssueView>();
        }

        var folderProfiles = await _jobStore.GetFolderProfilesForJobAsync(CurrentJobId);
        var effectiveProfiles = BuildEffectiveProfilesForValidation(folders, folderProfiles);
        var overriddenFolderIds = new HashSet<string>(
            folders.Where(f => string.Equals(f.ProfileMode, "override", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.FolderId),
            StringComparer.OrdinalIgnoreCase);

        var folderById = folders.ToDictionary(f => f.FolderId, StringComparer.OrdinalIgnoreCase);
        var lookupFields = _schema.Fields.Where(f => f.IsLookup).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var requiredFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_jobContext.CabinetId))
        {
            var syncedAttributes = await _jobStore.GetNetDocumentsAttributesAsync(_jobContext.CabinetId);
            foreach (var attribute in syncedAttributes.Where(a => a.IsRequired))
            {
                if (!string.IsNullOrWhiteSpace(attribute.Name))
                {
                    requiredFieldNames.Add(attribute.Name);
                }
            }
        }

        var issues = new List<ReviewValidationIssueView>();
        foreach (var (folderId, profileEntries) in effectiveProfiles)
        {
            if (!folderById.TryGetValue(folderId, out var folder))
            {
                continue;
            }

            var isRoot = folder.Depth == 0;
            var includeScope = isRoot || overriddenFolderIds.Contains(folderId);
            if (!includeScope)
            {
                continue;
            }

            var scope = isRoot ? ReviewValidationScope.Job : ReviewValidationScope.Folder;
            var target = isRoot ? "Job defaults" : folder.RelativePath;

            var resolvedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in profileEntries)
            {
                var fieldName = ResolveFieldNameForValidation(entry, _schema);
                if (string.IsNullOrWhiteSpace(fieldName))
                {
                    continue;
                }

                if (entry.Mode == ProfileFieldMode.Label &&
                    _schema.TryResolveCanonicalFieldName(entry.Field, out var canonical) &&
                    !string.Equals(entry.Field.Trim(), canonical, StringComparison.Ordinal))
                {
                    issues.Add(new ReviewValidationIssueView(
                        ReviewValidationSeverity.Warning,
                        scope,
                        target,
                        $"Attribute name casing does not match exactly: '{entry.Field}' should be '{canonical}'."));
                }

                if (entry.Mode == ProfileFieldMode.Label &&
                    !_schema.TryResolveCanonicalFieldName(entry.Field, out _) &&
                    !_schema.TryResolveFieldName(entry.Field, out _))
                {
                    issues.Add(new ReviewValidationIssueView(
                        ReviewValidationSeverity.Warning,
                        scope,
                        target,
                        $"Unknown attribute '{entry.Field}'."));
                }

                resolvedValues[fieldName] = entry.Value;

                if (entry.Value.Contains(';', StringComparison.Ordinal))
                {
                    issues.Add(new ReviewValidationIssueView(
                        ReviewValidationSeverity.Warning,
                        scope,
                        target,
                        $"Value for '{fieldName}' contains ';'. Export joins multi-values with '; ' and escapes inline semicolons as '{{;}}'."));
                }

                if (!lookupFields.TryGetValue(fieldName, out var lookupField))
                {
                    continue;
                }

                var tokens = SplitLookupTokens(entry.Value, lookupField.IsMultiValue);
                foreach (var token in tokens)
                {
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    if (lookupField.ValuesByCode.ContainsKey(token))
                    {
                        continue;
                    }

                    if (lookupField.ValuesByLabel.ContainsKey(token))
                    {
                        issues.Add(new ReviewValidationIssueView(
                            ReviewValidationSeverity.Warning,
                            scope,
                            target,
                            $"Lookup value for '{fieldName}' appears to use description '{token}' instead of key."));
                        continue;
                    }

                    issues.Add(new ReviewValidationIssueView(
                        ReviewValidationSeverity.Error,
                        scope,
                        target,
                        $"Invalid lookup key '{token}' for '{fieldName}'."));
                }
            }

            foreach (var requiredFieldName in requiredFieldNames)
            {
                if (resolvedValues.TryGetValue(requiredFieldName, out var value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                issues.Add(new ReviewValidationIssueView(
                    ReviewValidationSeverity.Error,
                    scope,
                    target,
                    $"Required attribute '{requiredFieldName}' is missing."));
            }
        }

        return issues;
    }

    private static Dictionary<string, IReadOnlyList<ProfileFieldEntry>> BuildEffectiveProfilesForValidation(
        IReadOnlyList<FolderRecord> folders,
        IReadOnlyDictionary<string, string> folderProfiles)
    {
        var effective = new Dictionary<string, IReadOnlyList<ProfileFieldEntry>>(StringComparer.OrdinalIgnoreCase);
        var ordered = folders
            .OrderBy(f => f.Depth)
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var folder in ordered)
        {
            if (string.Equals(folder.ProfileMode, "override", StringComparison.OrdinalIgnoreCase) &&
                folderProfiles.TryGetValue(folder.FolderId, out var payload))
            {
                effective[folder.FolderId] = ProfilePayloadCodec.Deserialize(payload);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(folder.ParentFolderId) &&
                effective.TryGetValue(folder.ParentFolderId, out var inherited))
            {
                effective[folder.FolderId] = inherited;
            }
            else
            {
                effective[folder.FolderId] = Array.Empty<ProfileFieldEntry>();
            }
        }

        return effective;
    }

    private static IReadOnlyList<string> SplitLookupTokens(string value, bool isMultiValue)
    {
        if (!isMultiValue)
        {
            return new[] { value.Trim() };
        }

        return value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static string ResolveFieldNameForValidation(ProfileFieldEntry entry, ProfileSchemaDictionary schema)
    {
        if (entry.Mode == ProfileFieldMode.Code)
        {
            return schema.TryResolveFieldName(entry.Field, out var name) ? name : entry.Field;
        }

        return schema.TryResolveCanonicalFieldName(entry.Field, out var canonical)
            ? canonical
            : entry.Field;
    }
}

public sealed class ImportJobContext
{
    public string JobId { get; set; } = string.Empty;

    public string SourceFolder { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public string RepositoryId { get; set; } = string.Empty;

    public string CabinetId { get; set; } = string.Empty;

    public string CabinetName { get; set; } = string.Empty;

    public string TargetContainerId { get; set; } = string.Empty;

    public string TargetContainerName { get; set; } = string.Empty;

    public NetDocumentsProfileContext? NetDocumentsProfileContext { get; set; }
}

public sealed class NetDocumentsProfileContext
{
    public NetDocumentsProfileContext(
        string cabinetId,
        string repositoryId,
        int attributeCount,
        int requiredAttributeCount,
        int lookupAttributeCount,
        int lookupValueCount,
        DateTime? lastSyncUtc,
        bool hasLookupCache)
    {
        CabinetId = cabinetId;
        RepositoryId = repositoryId;
        AttributeCount = attributeCount;
        RequiredAttributeCount = requiredAttributeCount;
        LookupAttributeCount = lookupAttributeCount;
        LookupValueCount = lookupValueCount;
        LastSyncUtc = lastSyncUtc;
        HasLookupCache = hasLookupCache;
    }

    public string CabinetId { get; }

    public string RepositoryId { get; }

    public int AttributeCount { get; }

    public int RequiredAttributeCount { get; }

    public int LookupAttributeCount { get; }

    public int LookupValueCount { get; }

    public DateTime? LastSyncUtc { get; }

    public bool HasLookupCache { get; }
}

public enum ReviewValidationSeverity
{
    Error,
    Warning,
    Info
}

public enum ReviewValidationScope
{
    Job,
    Folder,
    File
}

public sealed class ReviewValidationIssueView
{
    public ReviewValidationIssueView(
        ReviewValidationSeverity severity,
        ReviewValidationScope scope,
        string target,
        string message)
    {
        Severity = severity;
        Scope = scope;
        Target = target;
        Message = message;
    }

    public ReviewValidationSeverity Severity { get; }

    public ReviewValidationScope Scope { get; }

    public string Target { get; }

    public string Message { get; }

    public int SeverityOrder => Severity switch
    {
        ReviewValidationSeverity.Error => 0,
        ReviewValidationSeverity.Warning => 1,
        _ => 2
    };

    public int ScopeOrder => Scope switch
    {
        ReviewValidationScope.Job => 0,
        ReviewValidationScope.Folder => 1,
        _ => 2
    };

    public string SeverityDisplay => Severity.ToString();

    public string ScopeDisplay => Scope.ToString();
}
