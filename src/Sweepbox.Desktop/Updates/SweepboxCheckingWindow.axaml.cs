using Avalonia.Controls;
using NetSparkleUpdater.Interfaces;

namespace Sweepbox.Desktop.Updates;

/// <summary>The small "checking" window shown while the update check runs.</summary>
public partial class SweepboxCheckingWindow : Window, ICheckingForUpdates
{
    public event EventHandler? UpdatesUIClosing;

    public SweepboxCheckingWindow(WindowIcon icon)
    {
        Icon = icon;
        InitializeComponent();
        Closing += (_, _) => UpdatesUIClosing?.Invoke(this, EventArgs.Empty);
    }

    void ICheckingForUpdates.Show() => Show();

    void ICheckingForUpdates.Close() => Close();
}
