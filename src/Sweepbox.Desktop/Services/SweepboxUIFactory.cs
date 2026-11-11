using Avalonia.Controls;
using NetSparkleUpdater;
using NetSparkleUpdater.Interfaces;
using NetSparkleUpdater.UI.Avalonia;
using NetSparkleUpdater.UI.Avalonia.ViewModels;
using Sweepbox.Desktop.Updates;

namespace Sweepbox.Desktop.Services;

/// <summary>NetSparkle windows in Sweepbox's own style; outcome messages go through the app's native notifications.</summary>
public sealed class SweepboxUIFactory : UIFactory
{
    private readonly WindowIcon _icon;

    public SweepboxUIFactory(WindowIcon icon) : base(icon) => _icon = icon;

    public override IUpdateAvailable CreateUpdateAvailableWindow(List<AppCastItem> updates, ISignatureVerifier? signatureVerifier,
        string currentVersion = "", string appName = "the application", bool isUpdateAlreadyDownloaded = false)
    {
        var viewModel = new UpdateAvailableWindowViewModel();
        if (ReleaseNotesGrabberOverride != null)
            viewModel.ReleaseNotesGrabber = ReleaseNotesGrabberOverride;
        viewModel.Initialize(updates, signatureVerifier, isUpdateAlreadyDownloaded, ReleaseNotesHTMLTemplate ?? "",
            AdditionalReleaseNotesHeaderHTML ?? "", ReleaseNotesDateTimeFormat, appName, currentVersion);
        if (HideReleaseNotes)
            viewModel.AreReleaseNotesVisible = false;
        if (HideRemindMeLaterButton)
            viewModel.IsRemindMeLaterVisible = false;
        if (HideSkipButton)
            viewModel.IsSkipVisible = false;
        return new SweepboxUpdateWindow(viewModel, _icon);
    }

    public override IDownloadProgress CreateProgressWindow(string downloadTitle, string actionButtonTitleAfterDownload)
    {
        var viewModel = new DownloadProgressWindowViewModel
        {
            DownloadingTitle = downloadTitle,
            ActionButtonTitle = actionButtonTitleAfterDownload,
        };
        return new SweepboxDownloadWindow(viewModel, _icon);
    }

    public override ICheckingForUpdates ShowCheckingForUpdates() => new SweepboxCheckingWindow(_icon);

    public override void ShowVersionIsUpToDate()
        => Notifier.Show("Sweepbox is up to date", "You are on the latest version.");

    public override void ShowVersionIsSkippedByUserRequest()
        => Notifier.Show("Update skipped", "You asked to skip this version. You can check again any time from the Sweepbox menu.");

    public override void ShowCannotDownloadAppcast(string? appcastUrl)
        => Notifier.Show("Could not check for updates", "GitHub could not be reached. Try again later.");

    public override void ShowDownloadErrorMessage(string message, string? appcastUrl)
        => Notifier.Show("Update failed", "The download did not complete. Check your connection and try again.");

    public override void ShowUnknownInstallerFormatMessage(string downloadFileName)
        => Notifier.Show("Update failed", $"The downloaded file has an unknown format: {downloadFileName}");
}
