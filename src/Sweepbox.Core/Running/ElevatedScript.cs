using System.Text;

namespace Sweepbox.Core.Running;

/// <summary>
/// Builds the batch file an elevated (UAC) Windows command runs through: elevated processes can't have
/// their output redirected, so the script does it. Every value pasted into it is validated, because the
/// script runs as administrator: anything that could break out of its quotes is refused.
/// </summary>
internal static class ElevatedScript
{
    /// <summary>Lines of the script, CRLF-separated. Throws <see cref="ArgumentException"/> for values that can't be passed safely.</summary>
    public static string Build(string path, IReadOnlyDictionary<string, string> environment, string executable,
        IReadOnlyList<string> arguments, string logFile)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append("\r\n");

        Line("@echo off");
        // The file is UTF-8; switch the code page before any line that may hold non-ASCII (user names in paths).
        Line("chcp 65001 >nul");
        Line($"set \"PATH={Escape(path, "PATH")}\"");
        foreach (var (key, value) in environment)
        {
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new ArgumentException($"Environment variable name '{key}' can't be passed to an elevated command.");
            Line($"set \"{key}={Escape(value, key)}\"");
        }
        var args = string.Concat(arguments.Select(a => $" \"{Escape(a, "argument")}\""));
        Line($"\"{Escape(executable, "executable")}\"{args} > \"{Escape(logFile, "log file")}\" 2>&1");
        Line("exit /b %ERRORLEVEL%");
        return sb.ToString();
    }

    /// <summary>
    /// True if <paramref name="executable"/> is an absolute path outside every folder the user can write to,
    /// so other programs running as the user can't swap what gets run as administrator.
    /// </summary>
    public static bool IsSafeExecutable(string executable, IEnumerable<string> userWritableRoots)
    {
        if (!Path.IsPathFullyQualified(executable))
            return false;
        var exe = Normalize(executable);
        return userWritableRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Normalize(r).TrimEnd('/') + "/")
            .All(root => !exe.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/');

    // Inside "…" in a batch file, & | < > ^ are literal; % still expands (doubled to escape it).
    // A quote, a line break or NUL would end the quoted value or the line, so they are refused.
    private static string Escape(string value, string what)
    {
        if (value.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
            throw new ArgumentException($"The {what} contains a character that can't be passed safely to an elevated command.");
        return value.Replace("%", "%%");
    }
}
