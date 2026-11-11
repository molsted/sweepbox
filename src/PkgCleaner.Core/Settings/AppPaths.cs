namespace PkgCleaner.Core.Settings;

public sealed class AppPaths
{
    public const string AppName = "PkgCleaner";

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

    public static AppPaths Default() => new(DefaultBaseDirectory());

    public static string DefaultBaseDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", AppName);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, AppName);
    }
}
