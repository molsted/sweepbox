using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;

namespace Sweepbox.Core.Managers;

public sealed class ChocoManager : PackageManager
{
    public override string Id => "choco";
    public override string DisplayName => "Chocolatey";
    public override string ExecutableName => "choco";
    public override bool IsSupportedOnThisOS => OperatingSystem.IsWindows();
    public override bool IsEnabled(AppSettings settings) => settings.Choco.Enabled;

    public override IReadOnlyList<CommandSpec> BuildSteps(AppSettings settings, string executable)
    {
        var s = settings.Choco;
        var steps = new List<CommandSpec>();
        void Add(string description, params string[] args) =>
            steps.Add(new CommandSpec(description, executable, args) { Elevate = s.ElevateWithUac });

        if (s.UpgradeAll)
        {
            var args = new List<string> { "upgrade", "all", "--yes", "--no-progress", "--limit-output" };
            var except = string.Join(',', s.Except.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (except.Length > 0)
                args.Add($"--except={except}");
            Add("Upgrade all packages", [.. args]);
        }
        if (s.CacheRemove)
            steps.Add(new CommandSpec("Remove download cache", executable, ["cache", "remove", "--yes"]) { Elevate = s.ElevateWithUac, IsCleanup = true });
        return steps;
    }

    /// <summary>Chocolatey caches downloads in the user's and the system's TEMP folder.</summary>
    public override Task<IReadOnlyList<string>> GetCleanupLocationsAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(
        [
            Path.Combine(Path.GetTempPath(), "chocolatey"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "chocolatey"),
        ]);
}
