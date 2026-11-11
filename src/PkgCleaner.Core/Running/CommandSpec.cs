namespace PkgCleaner.Core.Running;

/// <summary>One command to run, e.g. <c>brew upgrade --cask</c>.</summary>
public sealed record CommandSpec(string Description, string Executable, IReadOnlyList<string> Arguments)
{
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Windows only: run with administrator rights (UAC prompt if the app itself is not elevated).</summary>
    public bool Elevate { get; init; }

    public string CommandLine => string.Join(' ', new[] { Path.GetFileName(Executable) }.Concat(Arguments.Select(Quote)));

    private static string Quote(string arg) => arg.Contains(' ') || arg.Length == 0 ? $"\"{arg}\"" : arg;
}

public sealed record CommandResult(int ExitCode, TimeSpan Duration, bool TimedOut = false, bool Cancelled = false, string? Error = null)
{
    public bool Success => ExitCode == 0 && !TimedOut && !Cancelled && Error is null;
}

public interface ICommandRunner
{
    /// <summary>Full path of a tool found on the user's PATH, or null if it is not installed.</summary>
    string? ResolveExecutable(string name);

    Task<CommandResult> RunAsync(CommandSpec spec, Action<string> onOutput, TimeSpan timeout, CancellationToken cancellationToken);
}
