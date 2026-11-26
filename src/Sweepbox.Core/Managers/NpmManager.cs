using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;

namespace Sweepbox.Core.Managers;

public sealed class NpmManager : PackageManager
{
    private static readonly Dictionary<string, string> Env = new()
    {
        ["npm_config_update_notifier"] = "false",
        ["npm_config_fund"] = "false",
        ["npm_config_audit"] = "false",
        ["npm_config_color"] = "false",
        ["npm_config_progress"] = "false",
    };

    public override string Id => "npm";
    public override string DisplayName => "npm (global)";
    public override string ShortName => "npm";
    public override string ExecutableName => "npm";
    public override bool IsSupportedOnThisOS => true;
    public override bool IsEnabled(AppSettings settings) => settings.Npm.Enabled;

    public override IReadOnlyList<CommandSpec> BuildSteps(AppSettings settings, string executable)
    {
        var s = settings.Npm;
        var steps = new List<CommandSpec>();
        void Add(string description, params string[] args) =>
            steps.Add(new CommandSpec(description, executable, args) { Environment = Env });

        if (s.UpdateGlobals) Add("Update global packages", "update", "--global");
        switch (s.CacheMode)
        {
            case NpmCacheMode.Verify: Clean("Verify and garbage-collect cache", "cache", "verify"); break;
            case NpmCacheMode.Clean: Clean("Clear cache", "cache", "clean", "--force"); break;
        }
        return steps;

        void Clean(string description, params string[] args) =>
            steps.Add(new CommandSpec(description, executable, args) { Environment = Env, IsCleanup = true });
    }

    public override async Task<IReadOnlyList<string>> GetCleanupLocationsAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken) =>
        await QueryPathAsync(runner, executable, Env, cancellationToken, "config", "get", "cache") is { } cache ? [cache] : [];
}
