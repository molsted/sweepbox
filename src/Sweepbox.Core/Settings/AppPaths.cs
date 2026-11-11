namespace Sweepbox.Core.Settings;

public sealed class AppPaths
{
    public const string AppName = "Sweepbox";

    public AppPaths(string baseDirectory)
    {
        BaseDirectory = baseDirectory;
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string BaseDirectory { get; }
    public string SettingsFile => Path.Combine(BaseDirectory, "settings.json");
    public string HistoryFile => Path.Combine(BaseDirectory, "history.json");
    public string LockFile => Path.Combine(BaseDirectory, ".lock");
    public string LogDirectory => Path.Combine(BaseDirectory, "logs");

    /// <summary>The app's previous name; its data folder is moved over on first start.</summary>
    private const string LegacyAppName = "PkgCleaner";

    public static AppPaths Default()
    {
        var dir = DefaultBaseDirectory(AppName);
        MigrateLegacy(DefaultBaseDirectory(LegacyAppName), dir);
        return new AppPaths(dir);
    }

    public static string DefaultBaseDirectory(string appName = AppName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", appName);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, appName);
    }

    internal static void MigrateLegacy(string legacyDir, string newDir)
    {
        if (!Directory.Exists(legacyDir) || Directory.Exists(newDir))
            return;
        try
        {
            Directory.Move(legacyDir, newDir);
            File.Delete(Path.Combine(newDir, ".lock"));
        }
        catch (IOException) { } // e.g. old version still running: start fresh
        catch (UnauthorizedAccessException) { }
    }
}
