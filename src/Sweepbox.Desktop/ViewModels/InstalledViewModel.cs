using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sweepbox.Core.Engine;
using Sweepbox.Core.Managers;
using Sweepbox.Core.Running;
using Sweepbox.Desktop.Services;

namespace Sweepbox.Desktop.ViewModels;

/// <summary>One installed package in the Installed tab.</summary>
public partial class PackageItem : ViewModelBase
{
    private readonly Action _checkedChanged;

    public PackageItem(InstalledPackage package, Action checkedChanged)
    {
        Package = package;
        _checkedChanged = checkedChanged;
    }

    public InstalledPackage Package { get; }
    public string Name => Package.Name;
    public string Version => Package.Version ?? "";

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>The measured size; null means unknown. Meaningful only once <see cref="SizeKnown"/> is true.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private PackageSize? _size;

    /// <summary>False until this package's manager has reported its sizes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private bool _sizeKnown;

    /// <summary>Empty until reported; "–" when unknown; "≈ 1.2 GB" for an estimate; otherwise "1.2 GB".</summary>
    public string SizeText => !SizeKnown ? ""
        : Size is not { } size ? "–"
        : size.IsEstimate ? "≈ " + DiskUsage.Format(size.Bytes)
        : DiskUsage.Format(size.Bytes);

    partial void OnIsCheckedChanged(bool value) => _checkedChanged();
}

/// <summary>One manager (and, for Homebrew, one kind) in the Installed tab.</summary>
public partial class PackageGroup : ViewModelBase
{
    public PackageGroup(string title, IEnumerable<PackageItem> items, string? error)
    {
        Title = title;
        Items = new ObservableCollection<PackageItem>(items);
        Error = error;
    }

    public string Title { get; }

    /// <summary>Re-sorted in place (by moving items), so the same <see cref="PackageItem"/> instances stay.</summary>
    public ObservableCollection<PackageItem> Items { get; }
    public int Count => Items.Count;
    public string? Error { get; }
    public bool HasError => Error is not null;

    /// <summary>An error group has no packages, so it shows no count.</summary>
    public bool ShowCount => !HasError;

    /// <summary>False when the search hides every item and there is no error to show.</summary>
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>
    /// Empty until every item has been reported, or when no size is known; otherwise the sum of the known sizes,
    /// prefixed with "≈ " if any of them is an estimate.
    /// </summary>
    public string TotalText
    {
        get
        {
            if (Items.Any(i => !i.SizeKnown))
                return "";
            var known = Items.Select(i => i.Size).OfType<PackageSize>().ToList();
            if (known.Count == 0)
                return "";
            var text = DiskUsage.Format(known.Sum(s => s.Bytes));
            return known.Any(s => s.IsEstimate) ? "≈ " + text : text;
        }
    }

    /// <summary>Call after changing items' sizes.</summary>
    public void SizesChanged() => OnPropertyChanged(nameof(TotalText));

    /// <summary>Sorts by size (largest first; unreported and unknown last, then by name) or by name.</summary>
    public void Sort(bool bySize)
    {
        var ordered = bySize
            ? Items.OrderBy(i => i is { SizeKnown: true, Size: not null } ? 0 : 1)
                .ThenByDescending(i => i.SizeKnown ? i.Size?.Bytes ?? 0 : 0)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        for (var target = 0; target < ordered.Count; target++)
        {
            var current = Items.IndexOf(ordered[target]);
            if (current != target)
                Items.Move(current, target);
        }
    }
}

/// <summary>The Installed tab: lists installed packages and uninstalls the ticked ones.</summary>
public partial class InstalledViewModel : ViewModelBase, IDisposable
{
    public const string BusyText = "Sweepbox is busy – the list will refresh when it finishes";

    private static readonly string[] GroupTitles =
        ["Homebrew – apps (casks)", "Homebrew – command-line tools", "Chocolatey", "npm (global)"];

    private readonly AppServices _services;
    private readonly Action _showLog;
    private readonly Action _clearLog;
    private readonly Action? _showInstalled;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _measureCts;
    // Incremented whenever Groups is rebuilt; size results for an older list are dropped.
    private int _generation;
    private Task? _loadTask;
    private bool _loadDeferred;
    private bool _disposed;

    /// <param name="showLog">Switches to the live log.</param>
    /// <param name="clearLog">Clears the live log; called from the engine once the uninstall run is accepted.</param>
    /// <param name="showInstalled">Switches back to the Installed tab, for when an uninstall can't start.</param>
    public InstalledViewModel(AppServices services, Action showLog, Action clearLog, Action? showInstalled = null)
    {
        _services = services;
        _showLog = showLog;
        _clearLog = clearLog;
        _showInstalled = showInstalled;

        services.Engine.StatusChanged += OnStatusChanged;
        services.Engine.RunCompleted += OnRunCompleted;
        IsBusy = services.Engine.Status.IsRunning;
    }

    public ObservableCollection<PackageGroup> Groups { get; } = [];

    /// <summary>Asks the user to confirm; null (headless) means "not confirmed".</summary>
    public Func<IReadOnlyList<InstalledPackage>, IReadOnlyList<string>, Task<bool>>? Confirm { get; set; }

    [ObservableProperty] private string _searchText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(UninstallSelectedCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(UninstallSelectedCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UninstallLabel))]
    [NotifyCanExecuteChangedFor(nameof(UninstallSelectedCommand))]
    private int _selectedCount;

    /// <summary>True while package sizes are being measured. Never blocks Refresh or Uninstall.</summary>
    [ObservableProperty] private bool _isMeasuring;

    /// <summary>Sort each group by size instead of by name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortByName))]
    private bool _sortBySize;

    /// <summary>The inverse of <see cref="SortBySize"/>, for the "Name" toggle.</summary>
    public bool SortByName
    {
        get => !SortBySize;
        set => SortBySize = !value;
    }

    /// <summary>The busy text, a listing error, or null.</summary>
    [ObservableProperty] private string? _statusMessage;

    public string UninstallLabel => $"Uninstall selected ({SelectedCount})";

    /// <summary>The group title a package is listed under, as in the confirmation dialog.</summary>
    public static string GroupTitle(InstalledPackage package) => (package.ManagerId, package.Kind) switch
    {
        ("brew", PackageKind.Cask) => GroupTitles[0],
        ("brew", _) => GroupTitles[1],
        ("choco", _) => GroupTitles[2],
        ("npm", _) => GroupTitles[3],
        _ => package.ManagerId,
    };

    /// <summary>"git (Chocolatey)", plus ", also removes git.install" when the package has companions.</summary>
    public static string ConfirmationLine(InstalledPackage package) =>
        $"{package.Name} ({GroupTitle(package)})" +
        (package.Companions is { Count: > 0 } companions ? ", also removes " + string.Join(", ", companions) : "");

    /// <summary>
    /// Starts the first listing; later calls return the same task. While a run is in progress the first
    /// listing waits: the reload when that run completes performs it.
    /// </summary>
    public Task EnsureLoadedAsync()
    {
        if (_loadTask is not null)
            return _loadTask;
        // Both this and the RunCompleted handler run on the UI thread. If the engine is running (or IsBusy hasn't
        // caught up with a run that just finished), RunCompleted's posted reload hasn't run yet and will see the flag.
        if (IsBusy || _services.Engine.Status.IsRunning)
        {
            _loadDeferred = true;
            return Task.CompletedTask;
        }
        return _loadTask = LoadAsync(keepTicks: false);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Refresh() => _loadTask = LoadAsync(keepTicks: false);

    private bool CanAct() => !IsBusy && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallSelectedAsync()
    {
        var packages = Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Package).ToList();
        if (packages.Count == 0)
            return;

        var notes = UninstallNotes.For(_services.Settings.Current, packages, Environment.IsPrivilegedProcess);
        if (Confirm is null || !await Confirm(packages, notes))
            return;

        // A run may have started while the dialog was open; don't clear its log.
        if (_services.Engine.Status.IsRunning)
        {
            StatusMessage = BusyText;
            return;
        }

        // Clear the log only once the engine has accepted this run (onStarted runs before its first log line),
        // so a run that started in the meantime keeps its log.
        // Measuring probes the package managers too; don't compete with the uninstall run.
        CancelMeasuring();
        _showLog();
        try
        {
            if (await _services.UninstallAsync(packages, _clearLog) is null)
            {
                // Another run started in the meantime: keep the ticks and say why nothing happened.
                StatusMessage = BusyText;
                _showInstalled?.Invoke();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not uninstall: {ex.Message}";
            _showInstalled?.Invoke();
        }
    }

    private bool CanUninstall() => CanAct() && SelectedCount > 0;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSortBySizeChanged(bool value)
    {
        foreach (var group in Groups)
            group.Sort(value);
    }

    partial void OnIsBusyChanged(bool value)
    {
        if (value)
            StatusMessage = BusyText;
        else if (StatusMessage == BusyText)
            StatusMessage = null;
    }

    /// <param name="keepTicks">Tick the same packages (by manager, name and kind) in the new list.</param>
    private async Task LoadAsync(bool keepTicks)
    {
        _loadCts?.Cancel();
        CancelMeasuring();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        if (StatusMessage != BusyText)
            StatusMessage = null;
        try
        {
            var inventories = await _services.Inventory.ListAsync(cts.Token);
            if (!cts.IsCancellationRequested)
            {
                BuildGroups(inventories, keepTicks);
                StartMeasuring(inventories);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Replaced by a newer listing, or the window closed.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                StatusMessage = $"Could not list packages: {ex.Message}";
        }
        finally
        {
            if (_loadCts == cts)
            {
                _loadCts = null;
                IsLoading = false;
            }
            cts.Dispose();
        }
    }

    private void BuildGroups(IReadOnlyList<ManagerInventory> inventories, bool keepTicks)
    {
        var ticked = keepTicks
            ? Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => Key(i.Package)).ToHashSet()
            : [];
        _generation++;
        Groups.Clear();
        foreach (var inventory in inventories.OrderBy(i => ManagerRank(i.ManagerId)))
        {
            var groups = inventory.Packages.GroupBy(GroupTitle)
                .Select(g => (Title: g.Key, Items: g
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new PackageItem(p, UpdateSelectedCount) { IsChecked = ticked.Contains(Key(p)) })
                    .ToList()))
                .ToList();
            var errorTitle = inventory.Error is null ? null : ErrorGroupTitle(inventory);
            if (errorTitle is not null && groups.All(g => g.Title != errorTitle))
                groups.Add((errorTitle, []));
            foreach (var (title, items) in groups.OrderBy(g => Rank(g.Title)))
                Groups.Add(new PackageGroup(title, items, title == errorTitle ? inventory.Error : null));
        }

        if (Groups.Count == 0 && StatusMessage is null)
            StatusMessage = "No supported package managers were found.";
        UpdateSelectedCount();
        ApplyFilter();

        static (string, string, PackageKind) Key(InstalledPackage p) => (p.ManagerId, p.Name, p.Kind);
    }

    private void StartMeasuring(IReadOnlyList<ManagerInventory> inventories)
    {
        CancelMeasuring();
        if (_disposed)
            return;
        var cts = _measureCts = new CancellationTokenSource();
        IsMeasuring = true;
        _ = MeasureAsync(inventories, _generation, cts);
    }

    private async Task MeasureAsync(IReadOnlyList<ManagerInventory> inventories, int generation, CancellationTokenSource cts)
    {
        try
        {
            // Measuring walks the disk; keep it off the UI thread. Results are applied on the UI thread.
            await Task.Run(() => _services.Inventory.MeasureAsync(inventories,
                sizes => Dispatcher.UIThread.Post(() => ApplySizes(sizes, generation)), cts.Token));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Replaced by a newer listing, an uninstall, or the window closed.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not measure package sizes: {ex.Message}");
        }
        finally
        {
            // Queued after the last ApplySizes post, so IsMeasuring clears once every result is shown.
            Dispatcher.UIThread.Post(() =>
            {
                if (_measureCts == cts)
                {
                    _measureCts = null;
                    if (!_disposed)
                        IsMeasuring = false;
                }
                cts.Dispose();
            });
        }
    }

    private void ApplySizes(IReadOnlyDictionary<PackageKey, PackageSize?> sizes, int generation)
    {
        if (_disposed || generation != _generation)
            return;
        foreach (var group in Groups)
        {
            var changed = false;
            foreach (var item in group.Items)
            {
                if (!sizes.TryGetValue(PackageKey.Of(item.Package), out var size))
                    continue;
                item.Size = size;
                item.SizeKnown = true;
                changed = true;
            }
            if (!changed)
                continue;
            group.SizesChanged();
            if (SortBySize)
                group.Sort(bySize: true);
        }
    }

    // Cancels a measurement in progress. IsMeasuring stays true until its task ends (or a new one replaces it).
    private void CancelMeasuring() => _measureCts?.Cancel();

    /// <summary>
    /// The group a listing error is shown in. A Homebrew error with casks listed means the formulae failed;
    /// otherwise the error is shown under the manager's first group.
    /// </summary>
    private static string ErrorGroupTitle(ManagerInventory inventory) => inventory.ManagerId switch
    {
        "brew" => inventory.Packages.Count > 0 ? GroupTitles[1] : GroupTitles[0],
        "choco" => GroupTitles[2],
        "npm" => GroupTitles[3],
        _ => inventory.DisplayName,
    };

    private static int ManagerRank(string managerId) => managerId switch
    {
        "brew" => 0,
        "choco" => 1,
        "npm" => 2,
        _ => int.MaxValue,
    };

    private static int Rank(string title) => Array.IndexOf(GroupTitles, title) is var i and >= 0 ? i : int.MaxValue;

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        foreach (var group in Groups)
        {
            foreach (var item in group.Items)
                item.IsVisible = query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            group.IsVisible = group.HasError || group.Items.Any(i => i.IsVisible);
        }
    }

    // Counts every ticked item, including those the search hides.
    private void UpdateSelectedCount() => SelectedCount = Groups.Sum(g => g.Items.Count(i => i.IsChecked));

    private void OnStatusChanged(EngineStatus _) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed)
            IsBusy = _services.Engine.Status.IsRunning;
    });

    private void OnRunCompleted(RunReport report) => Dispatcher.UIThread.Post(() =>
    {
        // Reload only if the list was shown (or asked for) before. Ticks survive a maintenance run;
        // after an uninstall they clear.
        if (_disposed || (_loadTask is null && !_loadDeferred))
            return;
        _loadDeferred = false;
        _loadTask = LoadAsync(keepTicks: report.Trigger != RunTrigger.Uninstall);
    });

    public void Dispose()
    {
        _disposed = true;
        _services.Engine.StatusChanged -= OnStatusChanged;
        _services.Engine.RunCompleted -= OnRunCompleted;
        _loadCts?.Cancel();
        CancelMeasuring();
    }
}
