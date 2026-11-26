using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sweepbox.Core.Engine;
using Sweepbox.Core.Managers;
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

    partial void OnIsCheckedChanged(bool value) => _checkedChanged();
}

/// <summary>One manager (and, for Homebrew, one kind) in the Installed tab.</summary>
public partial class PackageGroup : ViewModelBase
{
    public PackageGroup(string title, IReadOnlyList<PackageItem> items, string? error)
    {
        Title = title;
        Items = items;
        Error = error;
    }

    public string Title { get; }
    public IReadOnlyList<PackageItem> Items { get; }
    public int Count => Items.Count;
    public string? Error { get; }
    public bool HasError => Error is not null;

    /// <summary>False when the search hides every item and there is no error to show.</summary>
    [ObservableProperty] private bool _isVisible = true;
}

/// <summary>The Installed tab: lists installed packages and uninstalls the ticked ones.</summary>
public partial class InstalledViewModel : ViewModelBase, IDisposable
{
    public const string BusyText = "Sweepbox is busy – the list will refresh when it finishes";

    private static readonly string[] GroupTitles =
        ["Homebrew – apps (casks)", "Homebrew – command-line tools", "Chocolatey", "npm (global)"];

    private readonly AppServices _services;
    private readonly Action _showLog;
    private readonly Action? _showInstalled;
    private CancellationTokenSource? _loadCts;
    private Task? _loadTask;
    private bool _disposed;

    /// <param name="showLog">Clears the live log and switches to it, as Run Now does.</param>
    /// <param name="showInstalled">Switches back to the Installed tab, for when an uninstall can't start.</param>
    public InstalledViewModel(AppServices services, Action showLog, Action? showInstalled = null)
    {
        _services = services;
        _showLog = showLog;
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

    /// <summary>Starts the first listing; later calls return the same task.</summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Refresh() => _loadTask = LoadAsync();

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

        _showLog();
        try
        {
            if (await _services.UninstallAsync(packages) is null)
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

    partial void OnIsBusyChanged(bool value)
    {
        if (value)
            StatusMessage = BusyText;
        else if (StatusMessage == BusyText)
            StatusMessage = null;
    }

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        if (StatusMessage != BusyText)
            StatusMessage = null;
        try
        {
            var inventories = await _services.Inventory.ListAsync(cts.Token);
            if (!cts.IsCancellationRequested)
                BuildGroups(inventories);
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

    private void BuildGroups(IReadOnlyList<ManagerInventory> inventories)
    {
        Groups.Clear();
        foreach (var inventory in inventories.OrderBy(i => ManagerRank(i.ManagerId)))
        {
            if (inventory.Packages.Count == 0)
            {
                if (inventory.Error is not null)
                    Groups.Add(new PackageGroup(inventory.DisplayName, [], inventory.Error));
                continue;
            }

            var error = inventory.Error;
            foreach (var group in inventory.Packages.GroupBy(GroupTitle).OrderBy(g => Rank(g.Key)))
            {
                var items = group
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new PackageItem(p, UpdateSelectedCount))
                    .ToList();
                Groups.Add(new PackageGroup(group.Key, items, error));
                error = null; // Show a partial-listing error once, on the manager's first group.
            }
        }

        if (Groups.Count == 0 && StatusMessage is null)
            StatusMessage = "No supported package managers were found.";
        UpdateSelectedCount();
        ApplyFilter();
    }

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

    private void OnRunCompleted(RunReport _) => Dispatcher.UIThread.Post(() =>
    {
        // Reload only if the list was shown before; rebuilding the groups clears the ticks.
        if (!_disposed && _loadTask is not null)
            _loadTask = LoadAsync();
    });

    public void Dispose()
    {
        _disposed = true;
        _services.Engine.StatusChanged -= OnStatusChanged;
        _services.Engine.RunCompleted -= OnRunCompleted;
        _loadCts?.Cancel();
    }
}
