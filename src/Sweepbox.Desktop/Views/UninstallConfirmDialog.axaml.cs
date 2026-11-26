using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sweepbox.Desktop.Views;

/// <summary>What the uninstall confirmation shows: one line per package, plus any notes.</summary>
public sealed record UninstallConfirmation(IReadOnlyList<string> Lines, IReadOnlyList<string> Notes)
{
    public string Summary => Lines.Count == 1 ? "1 package will be removed." : $"{Lines.Count} packages will be removed.";
    public bool HasNotes => Notes.Count > 0;
}

/// <summary>Asks before uninstalling the ticked packages. Closes with true to go ahead.</summary>
public partial class UninstallConfirmDialog : Window
{
    public UninstallConfirmDialog() => InitializeComponent();

    private void OnUninstall(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
