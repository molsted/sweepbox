using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using NetSparkleUpdater;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.Events;
using NetSparkleUpdater.Interfaces;
using NetSparkleUpdater.UI.Avalonia.Interfaces;
using NetSparkleUpdater.UI.Avalonia.ViewModels;

namespace Sweepbox.Desktop.Updates;

/// <summary>The "update available" window in Sweepbox's own style; reuses NetSparkle's view model for all logic.</summary>
public partial class SweepboxUpdateWindow : Window, IUpdateAvailable, IReleaseNotesDisplayer, IUserRespondedToUpdateCheck
{
    private readonly UpdateAvailableWindowViewModel _vm;
    private bool _wasResponseSent;

    public event UserRespondedToUpdate? UserResponded;

    public SweepboxUpdateWindow(UpdateAvailableWindowViewModel vm, WindowIcon icon)
    {
        _vm = vm;
        DataContext = vm;
        Icon = icon;
        InitializeComponent();
        _vm.ReleaseNotesDisplayer = this;
        _vm.UserRespondedHandler = this;
        NotesText = PlainText(vm.Updates?.FirstOrDefault()?.Description);
        Closing += OnClosing;
    }

    private string NotesText
    {
        get => (string?)_notesText.Text ?? "";
        set
        {
            _notesText.Text = value;
            _vm.AreReleaseNotesVisible = !string.IsNullOrWhiteSpace(value);
        }
    }

    UpdateAvailableResult IUpdateAvailable.Result => _vm.UserResponse;

    AppCastItem IUpdateAvailable.CurrentItem => _vm.Updates?.FirstOrDefault() ?? new AppCastItem();

    public void UserRespondedToUpdateCheck(UpdateAvailableResult response) => SendResponse(response);

    public void ShowReleaseNotes(string htmlNotes) =>
        NotesText = string.IsNullOrWhiteSpace(htmlNotes) ? "" : PlainText(htmlNotes);

    void IUpdateAvailable.Show() => Show();

    void IUpdateAvailable.BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    void IUpdateAvailable.Close()
    {
        SendResponse(UpdateAvailableResult.None);
        Closing -= OnClosing;
        Close();
    }

    void IUpdateAvailable.HideReleaseNotes() => _vm.AreReleaseNotesVisible = false;

    void IUpdateAvailable.HideRemindMeLaterButton() => _vm.IsRemindMeLaterVisible = false;

    void IUpdateAvailable.HideSkipButton() => _vm.IsSkipVisible = false;

    private void SendResponse(UpdateAvailableResult response)
    {
        if (_wasResponseSent)
            return;
        _wasResponseSent = true;
        UserResponded?.Invoke(this, new UpdateResponseEventArgs(response, ((IUpdateAvailable)this).CurrentItem));
    }

    private void OnClosing(object? sender, CancelEventArgs e) => SendResponse(UpdateAvailableResult.None);

    /// <summary>Cut-down HTML to plain text; the appcast rarely carries notes, so a full renderer isn't worth it.</summary>
    private static string PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var text = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</(p|li|h\\d|div)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        return text.Trim();
    }
}
