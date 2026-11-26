using Avalonia.Controls;
using Sweepbox.Desktop.ViewModels;

namespace Sweepbox.Desktop.Views;

public partial class StatusWindow : Window
{
    public StatusWindow()
    {
        InitializeComponent();
        // Keep the live log scrolled to the newest line.
        LogBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // The confirmation is modal over this window.
        if (DataContext is StatusViewModel vm)
            vm.Installed.Confirm = (packages, notes) => new UninstallConfirmDialog
            {
                DataContext = new UninstallConfirmation(
                    packages.Select(InstalledViewModel.ConfirmationLine).ToList(),
                    notes),
            }.ShowDialog<bool>(this);
    }
}
