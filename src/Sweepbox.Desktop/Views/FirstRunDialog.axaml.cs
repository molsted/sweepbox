using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sweepbox.Desktop.Views;

/// <summary>First-run question: should Sweepbox start automatically at login?</summary>
public partial class FirstRunDialog : Window
{
    public FirstRunDialog() => InitializeComponent();

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);
}
