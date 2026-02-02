using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NetDocsImporter.Data;

namespace NetDocsImporter.Core;

public interface IFolderTreeProvider
{
    Task<IReadOnlyList<FolderRecord>> GetChildFoldersAsync(string jobId, string? parentFolderId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FileRecord>> GetChildFilesAsync(string jobId, string folderId, int limit, CancellationToken cancellationToken);

    Task UpdateFolderOverrideAsync(string folderId, bool isOverride, bool isIncluded, CancellationToken cancellationToken);

    Task AddFolderRuleAsync(string jobId, string folderId, string ruleType, string scope, string? notes, CancellationToken cancellationToken);
}

public sealed class JobStoreFolderTreeProvider : IFolderTreeProvider
{
    private readonly JobStore _jobStore;

    public JobStoreFolderTreeProvider(JobStore jobStore)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
    }

    public Task<IReadOnlyList<FolderRecord>> GetChildFoldersAsync(string jobId, string? parentFolderId, CancellationToken cancellationToken)
    {
        return _jobStore.GetChildFoldersAsync(jobId, parentFolderId, cancellationToken);
    }

    public Task<IReadOnlyList<FileRecord>> GetChildFilesAsync(string jobId, string folderId, int limit, CancellationToken cancellationToken)
    {
        return _jobStore.GetChildFilesAsync(jobId, folderId, limit, cancellationToken);
    }

    public Task UpdateFolderOverrideAsync(string folderId, bool isOverride, bool isIncluded, CancellationToken cancellationToken)
    {
        return _jobStore.UpdateFolderOverrideAsync(folderId, isOverride, isIncluded, cancellationToken);
    }

    public Task AddFolderRuleAsync(string jobId, string folderId, string ruleType, string scope, string? notes, CancellationToken cancellationToken)
    {
        return _jobStore.AddFolderRuleAsync(jobId, folderId, ruleType, scope, notes, cancellationToken);
    }
}

public abstract class TreeNodeBase : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isLoading;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; protected set; } = string.Empty;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        protected set => SetField(ref _isLoading, value);
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class FolderNodeViewModel : TreeNodeBase
{
    private readonly IFolderTreeProvider _provider;
    private readonly Action<Action> _dispatch;
    private readonly int _filePreviewLimit;
    private CancellationTokenSource? _loadCts;
    private bool _childrenLoaded;
    private bool _isIncluded;
    private bool _isOverride;
    private bool _effectiveIncluded;

    public FolderNodeViewModel(
        IFolderTreeProvider provider,
        Action<Action> dispatch,
        string jobId,
        FolderRecord record,
        FolderNodeViewModel? parent,
        int filePreviewLimit)
    {
        _provider = provider;
        _dispatch = dispatch;
        JobId = jobId;
        FolderId = record.FolderId;
        FullPath = record.FullPath;
        RelativePath = record.RelativePath;
        Parent = parent;
        Depth = record.Depth;
        _filePreviewLimit = filePreviewLimit;

        Name = string.IsNullOrWhiteSpace(record.RelativePath)
            ? record.FullPath
            : Path.GetFileName(record.FullPath);

        _isIncluded = record.IsIncluded;
        _isOverride = record.IsOverride;
        _effectiveIncluded = _isOverride ? _isIncluded : parent?.EffectiveIncluded ?? true;
    }

    public string JobId { get; }

    public string FolderId { get; }

    public string FullPath { get; }

    public string RelativePath { get; }

    public int Depth { get; }

    public FolderNodeViewModel? Parent { get; }

    public ObservableCollection<TreeNodeBase> Children { get; } = new();

    public bool IsOverride
    {
        get => _isOverride;
        private set => SetField(ref _isOverride, value);
    }

    public bool IsIncluded
    {
        get => _isIncluded;
        private set => SetField(ref _isIncluded, value);
    }

    public bool EffectiveIncluded
    {
        get => _effectiveIncluded;
        private set => SetField(ref _effectiveIncluded, value);
    }

    public async Task EnsureChildrenLoadedAsync(CancellationToken cancellationToken)
    {
        if (_childrenLoaded || IsLoading)
        {
            return;
        }

        _loadCts?.Cancel();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        IsLoading = true;
        try
        {
            var folders = await _provider.GetChildFoldersAsync(JobId, FolderId, _loadCts.Token);
            var files = await _provider.GetChildFilesAsync(JobId, FolderId, _filePreviewLimit, _loadCts.Token);

            _dispatch(() =>
            {
                Children.Clear();
                foreach (var folder in folders)
                {
                    Children.Add(new FolderNodeViewModel(_provider, _dispatch, JobId, folder, this, _filePreviewLimit));
                }

                foreach (var file in files)
                {
                    Children.Add(new FileNodeViewModel(file, EffectiveIncluded));
                }
            });

            _childrenLoaded = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void CancelLoading()
    {
        _loadCts?.Cancel();
    }

    public async Task ToggleIncludeAsync(CancellationToken cancellationToken)
    {
        await _provider.UpdateFolderOverrideAsync(FolderId, true, true, cancellationToken);
        await _provider.AddFolderRuleAsync(JobId, FolderId, "Include", "ThisAndChildren", null, cancellationToken);

        IsOverride = true;
        IsIncluded = true;
        RecalculateEffectiveIncluded();
    }

    public async Task ToggleExcludeAsync(CancellationToken cancellationToken)
    {
        await _provider.UpdateFolderOverrideAsync(FolderId, true, false, cancellationToken);
        await _provider.AddFolderRuleAsync(JobId, FolderId, "Exclude", "ThisAndChildren", null, cancellationToken);

        IsOverride = true;
        IsIncluded = false;
        RecalculateEffectiveIncluded();
    }

    public async Task ClearOverrideAsync(CancellationToken cancellationToken)
    {
        await _provider.UpdateFolderOverrideAsync(FolderId, false, true, cancellationToken);
        await _provider.AddFolderRuleAsync(JobId, FolderId, "Include", "ThisAndChildren", "Cleared override", cancellationToken);

        IsOverride = false;
        IsIncluded = true;
        RecalculateEffectiveIncluded();
    }

    public void RecalculateEffectiveIncluded()
    {
        EffectiveIncluded = IsOverride ? IsIncluded : Parent?.EffectiveIncluded ?? true;

        foreach (var child in Children.OfType<FolderNodeViewModel>())
        {
            if (!child.IsOverride)
            {
                child.RecalculateEffectiveIncluded();
            }
        }

        foreach (var child in Children.OfType<FileNodeViewModel>())
        {
            child.SetEffectiveIncluded(EffectiveIncluded);
        }
    }
}

public sealed class FileNodeViewModel : TreeNodeBase
{
    private bool _effectiveIncluded;

    public FileNodeViewModel(FileRecord record, bool effectiveIncluded)
    {
        Name = Path.GetFileName(record.FullPath);
        FullPath = record.FullPath;
        RelativePath = record.RelativePath;
        _effectiveIncluded = effectiveIncluded;
    }

    public string FullPath { get; }

    public string RelativePath { get; }

    public bool EffectiveIncluded
    {
        get => _effectiveIncluded;
        private set => SetField(ref _effectiveIncluded, value);
    }

    public void SetEffectiveIncluded(bool included)
    {
        EffectiveIncluded = included;
    }
}
