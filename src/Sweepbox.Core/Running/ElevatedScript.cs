using System.Text;

namespace Sweepbox.Core.Running;

/// <summary>
/// Builds the batch file an elevated (UAC) Windows command runs through: elevated processes can't have
/// their output redirected, so the script does it. Every value pasted into it is validated, because the
/// script runs as administrator: anything that could break out of its quotes is refused.
/// </summary>
internal static class ElevatedScript
{
    private static readonly char[] Unsafe = ['"', '\r', '\n', '\0'];

    /// <summary>Lines of the script, CRLF-separated. Throws <see cref="ArgumentException"/> for values that can't be passed safely.</summary>
    public static string Build(string path, IReadOnlyDictionary<string, string> environment, string executable,
        IReadOnlyList<string> arguments, string logFile, string systemDirectory)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append("\r\n");

        Line("@echo off");
        // The file is UTF-8; switch the code page before any line that may hold non-ASCII (user names in paths).
        Line($"\"{Escape(systemDirectory.TrimEnd('\\'), "system folder")}\\chcp.com\" 65001 >nul");
        Line($"set \"PATH={Escape(path, "PATH")}\"");
        foreach (var (key, value) in environment)
        {
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new ArgumentException($"Environment variable name '{key}' can't be passed to an elevated command.");
            Line($"set \"{key}={Escape(value, key)}\"");
        }
        var args = string.Concat(arguments.Select(a => $" \"{QuoteBody(Escape(a, "argument"))}\""));
        Line($"\"{Escape(executable, "executable")}\"{args} > \"{Escape(logFile, "log file")}\" 2>&1");
        Line("exit /b %ERRORLEVEL%");
        return sb.ToString();
    }

    /// <summary>
    /// A PATH for the elevated command: quotes around entries are removed (Windows allows them; the script can't
    /// carry them), and entries that still hold an unsafe character are dropped.
    /// </summary>
    public static string SafePath(string? path) => string.Join(';',
        (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.Length >= 2 && e[0] == '"' && e[^1] == '"' ? e[1..^1].Trim() : e)
            .Where(e => e.Length > 0 && e.IndexOfAny(Unsafe) < 0));

    /// <summary>
    /// The only program Sweepbox runs as administrator is Chocolatey, and only the machine-wide install
    /// (the machine <c>ChocolateyInstall</c> folder, which only administrators can change). Returns its
    /// <c>bin\choco.exe</c> when <paramref name="requested"/> is choco, else null. The copy found on the
    /// user's PATH is ignored: a folder on it could be one the user (or a program running as them) controls.
    /// </summary>
    public static string? MachineChocoExecutable(string? machineInstallDir, string requested)
    {
        if (string.IsNullOrWhiteSpace(machineInstallDir) || !Path.IsPathFullyQualified(machineInstallDir)
            || machineInstallDir.IndexOfAny(Unsafe) >= 0)
            return null;
        var name = requested.Replace('\\', '/').Split('/')[^1];
        if (!name.Equals("choco.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("choco", StringComparison.OrdinalIgnoreCase))
            return null;
        return machineInstallDir.TrimEnd('\\', '/') + "\\bin\\choco.exe";
    }

    // Inside "…" in a batch file, & | < > ^ are literal; % still expands (doubled to escape it).
    // A quote, a line break or NUL would end the quoted value or the line, so they are refused.
    private static string Escape(string value, string what)
    {
        if (value.IndexOfAny(Unsafe) >= 0)
            throw new ArgumentException($"The {what} contains a character that can't be passed safely to an elevated command.");
        return value.Replace("%", "%%");
    }

    // The child parses its command line with the usual rules: backslashes right before the closing quote
    // must be doubled, or \" would read as an escaped quote and merge this argument with the next.
    private static string QuoteBody(string value)
    {
        var trailing = value.Length - value.TrimEnd('\\').Length;
        return trailing == 0 ? value : value + new string('\\', trailing);
    }
}
