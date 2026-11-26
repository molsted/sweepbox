using Sweepbox.Core.Settings;

namespace Sweepbox.Core.Managers;

/// <summary>Extra lines for the uninstall confirmation, based on what is selected and how it will run.</summary>
public static class UninstallNotes
{
    public static IReadOnlyList<string> For(AppSettings settings, IReadOnlyCollection<InstalledPackage> packages, bool isElevated)
    {
        var notes = new List<string>();
        if (settings.Brew.Autoremove && packages.Any(p => p.ManagerId == "brew"))
            notes.Add("Homebrew will also remove dependencies that nothing else uses.");
        if (settings.Choco.ElevateWithUac && !isElevated && packages.Any(p => p.ManagerId == "choco"))
            notes.Add("You'll be asked for administrator permission once per Chocolatey package.");
        return notes;
    }
}
