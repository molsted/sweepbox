using Avalonia.Controls;

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
}
