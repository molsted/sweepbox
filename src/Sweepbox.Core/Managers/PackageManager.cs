using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;

namespace Sweepbox.Core.Managers;

public abstract class PackageManager
{
    /// <summary>Short id, e.g. "brew".</summary>
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    /// <summary>Name used in running text, e.g. "npm" rather than "npm (global)".</summary>
    public virtual string ShortName => DisplayName;

    /// <summary>Name of the executable to look up on PATH.</summary>
    public abstract string ExecutableName { get; }

    public abstract bool IsSupportedOnThisOS { get; }

    public abstract bool IsEnabled(AppSettings settings);

    /// <summary>The commands for one maintenance run, in order. Steps that free disk space have IsCleanup set.</summary>
    public abstract IReadOnlyList<CommandSpec> BuildSteps(AppSettings settings, string executable);

    /// <summary>
    /// Directories that cleanup steps shrink. Their total size is measured before the first and after the
    /// last cleanup step to report how much space was freed.
    /// </summary>
    public virtual Task<IReadOnlyList<string>> GetCleanupLocationsAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>What this manager has installed. The default is an empty inventory.</summary>
    public virtual Task<ManagerInventory> ListInstalledAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken) =>
        Task.FromResult(new ManagerInventory(Id, DisplayName, [], null));

    /// <summary>The command that uninstalls one package.</summary>
    public virtual CommandSpec BuildUninstallStep(AppSettings settings, string executable, InstalledPackage package) =>
        throw new NotSupportedException($"{ShortName} can't uninstall packages.");

    /// <summary>True for packages that must not be offered for uninstall.</summary>
    public virtual bool IsProtected(string packageName) => false;

    /// <summary>How long a listing probe may take.</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(60);

    protected sealed record ProbeOutput(CommandResult Result, IReadOnlyList<string> Lines)
    {
        public int ExitCode => Result.ExitCode;
        public bool Succeeded => Result.Success;
    }

    /// <summary>Runs a quick query command and returns its result and output lines.</summary>
    protected static async Task<ProbeOutput> ProbeAsync(ICommandRunner runner, string executable,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken, params string[] args) =>
        await ProbeAsync(runner, executable, environment, ListTimeout, cancellationToken, args);

    private static async Task<ProbeOutput> ProbeAsync(ICommandRunner runner, string executable,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken, string[] args)
    {
        var lines = new List<string>();
        var spec = new CommandSpec("Query " + string.Join(' ', args), executable, args) { Environment = environment, IsProbe = true };
        var result = await runner.RunAsync(spec, chunk => lines.AddRange(chunk.Split('\n')), timeout, cancellationToken);
        return new ProbeOutput(result, lines);
    }

    /// <summary>An inventory with the given packages (usually none) and an error saying why listing failed.</summary>
    protected ManagerInventory ListFailed(string command, ProbeOutput output, IReadOnlyList<InstalledPackage>? packages = null)
    {
        var reason = output.Result.TimedOut
            ? $" (timed out after {ListTimeout.TotalSeconds:0} s)."
            : $" (exit {output.ExitCode})" + (LastLine(output) is { } last ? ": " + last : "") + ".";
        System.Diagnostics.Trace.WriteLine(
            $"{ShortName} listing failed: {command}{reason}{Environment.NewLine}{string.Join(Environment.NewLine, output.Lines)}");
        return new(Id, DisplayName, packages ?? [], $"Couldn't list {ShortName} packages: \"{command}\" failed{reason}");

        static string? LastLine(ProbeOutput output) =>
            output.Lines.Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? output.Result.Error;
    }

    /// <summary>Runs a quick query command (e.g. <c>brew --cache</c>) and returns its last line if it is an absolute path.</summary>
    protected static async Task<string?> QueryPathAsync(ICommandRunner runner, string executable,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken, params string[] args)
    {
        var output = await ProbeAsync(runner, executable, environment, TimeSpan.FromSeconds(30), cancellationToken, args);
        var last = output.Lines.Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return output.ExitCode == 0 && last is not null && Path.IsPathRooted(last) ? last : null;
    }

    public static IReadOnlyList<PackageManager> All { get; } = [new BrewManager(), new ChocoManager(), new NpmManager()];

    public static IReadOnlyList<PackageManager> ForThisOS => All.Where(m => m.IsSupportedOnThisOS).ToList();

    /// <summary>"Homebrew and npm" / "Chocolatey and npm" - for UI text that should match the platform.</summary>
    public static string JoinNames(IEnumerable<PackageManager> managers)
    {
        var names = managers.Select(m => m.ShortName).ToList();
        return names.Count switch
        {
            0 => "your package managers",
            1 => names[0],
            _ => string.Join(", ", names[..^1]) + " and " + names[^1],
        };
    }
}
