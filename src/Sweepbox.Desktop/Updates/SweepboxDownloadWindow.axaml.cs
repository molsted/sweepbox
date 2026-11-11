using Avalonia.Controls;
using Avalonia.Interactivity;
using NetSparkleUpdater.Events;
using NetSparkleUpdater.Interfaces;
using NetSparkleUpdater.UI.Avalonia.ViewModels;

namespace Sweepbox.Desktop.Updates;

/// <summary>Download progress in Sweepbox's own style; reuses NetSparkle's view model.</summary>
public partial class SweepboxDownloadWindow : Window, IDownloadProgress
{
    private readonly DownloadProgressWindowViewModel _vm;

    public event DownloadInstallEventHandler? DownloadProcessCompleted;

    public SweepboxDownloadWindow(DownloadProgressWindowViewModel vm, WindowIcon icon)
    {
        _vm = vm;
        DataContext = vm;
        Icon = icon;
        InitializeComponent();
    }

    void IDownloadProgress.SetDownloadAndInstallButtonEnabled(bool shouldBeEnabled)
        => _installButton.IsEnabled = shouldBeEnabled;

    void IDownloadProgress.Show() => Show();

    void IDownloadProgress.Close() => Close();

    void IDownloadProgress.OnDownloadProgressChanged(object? sender, ItemDownloadProgressEventArgs args)
        => _vm.UpdateProgress(args.BytesReceived, args.TotalBytesToReceive, args.ProgressPercentage);

    void IDownloadProgress.FinishedDownloadingFile(bool isDownloadedFileValid)
    {
        _vm.SetFinishedDownloading(isDownloadedFileValid);
        _installButton.IsEnabled = isDownloadedFileValid;
    }

    bool IDownloadProgress.DisplayErrorMessage(string errorMessage)
    {
        _vm.ErrorMessageText = errorMessage;
        _vm.IsErrorMessageVisible = true;
        return true;
    }

    private void OnInstallClick(object? sender, RoutedEventArgs e)
        => DownloadProcessCompleted?.Invoke(this, new DownloadInstallEventArgs(shouldInstall: true));
}
