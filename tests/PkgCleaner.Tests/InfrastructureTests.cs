using PkgCleaner.Core.Engine;
using PkgCleaner.Core.Running;
using PkgCleaner.Core.Settings;

namespace PkgCleaner.Tests;

public sealed class InfrastructureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pkgcleaner-tests-").FullName;

    [Fact]
    public void Settings_round_trip_and_are_normalized()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        var s = store.Current;
        s.IntervalHours = 0; // invalid, clamps to 1
        s.Brew.Greedy = true;
        s.Npm.CacheMode = NpmCacheMode.Clean;
        store.Save(s);

        var reloaded = new SettingsStore(path).Current;
        Assert.Equal(1, reloaded.IntervalHours);
        Assert.True(reloaded.Brew.Greedy);
        Assert.Equal(NpmCacheMode.Clean, reloaded.Npm.CacheMode);
        Assert.Contains("\"Clean\"", File.ReadAllText(path)); // enums stored readably
    }

    [Fact]
    public void Current_returns_a_copy()
    {
        var store = new SettingsStore(Path.Combine(_dir, "s.json"));
        store.Current.Brew.Enabled = false;
        Assert.True(store.Current.Brew.Enabled);
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults_and_keep_a_copy()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");

        Assert.Equal(24, new SettingsStore(path).Current.IntervalHours);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void History_keeps_only_the_newest_entries()
    {
        var path = Path.Combine(_dir, "history.json");
        var store = new RunHistoryStore(path, maxEntries: 3);
        for (var i = 0; i < 5; i++)
            store.Add(new RunReport { StartedAt = DateTimeOffset.UnixEpoch.AddDays(i) });

        var reloaded = new RunHistoryStore(path);
        Assert.Equal(3, reloaded.Runs.Count);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(4), reloaded.Latest!.StartedAt);
    }

    [Theory]
    [InlineData("noise\n__PKGCLEANER_PATH__/a:/b__PKGCLEANER_PATH__\nmore", "/a:/b")]
    [InlineData("no marker here", null)]
    [InlineData("__PKGCLEANER_PATH__unterminated", null)]
    public void Parses_path_from_login_shell_output(string output, string? expected) =>
        Assert.Equal(expected, ShellEnvironment.ParseMarkedPath(output));

    [Fact]
    public void Finds_executables_on_a_custom_path()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "bin")).FullName;
        var name = OperatingSystem.IsWindows() ? "tool.cmd" : "tool";
        File.WriteAllText(Path.Combine(bin, name), "");

        Assert.Equal(Path.Combine(bin, name), ShellEnvironment.FindExecutable("tool", "/nope" + Path.PathSeparator + bin));
        Assert.Null(ShellEnvironment.FindExecutable("missing", bin));
    }

    [Fact]
    public async Task Resolved_path_contains_homebrew_fallbacks_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = await ShellEnvironment.ResolvePathAsync("/custom/dir");
        var dirs = path.Split(Path.PathSeparator);
        Assert.Equal("/custom/dir", dirs[0]);
        Assert.Contains("/opt/homebrew/bin", dirs);
        Assert.Equal(dirs.Length, dirs.Distinct().Count());
    }

    [Fact]
    public async Task Process_runner_captures_output_and_exit_code()
    {
        if (OperatingSystem.IsWindows())
            return;
        var runner = new ProcessCommandRunner("/usr/bin:/bin");
        var lines = new List<string>();

        var ok = await runner.RunAsync(new CommandSpec("echo", "/bin/sh", ["-c", "echo hello; echo oops >&2; exit 3"]),
            lines.Add, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(3, ok.ExitCode);
        Assert.False(ok.Success);
        Assert.Contains("hello", lines);
        Assert.Contains("oops", lines);
    }

    [Fact]
    public async Task Process_runner_passes_environment_and_closes_stdin()
    {
        if (OperatingSystem.IsWindows())
            return;
        var runner = new ProcessCommandRunner("/usr/bin:/bin");
        var lines = new List<string>();
        var spec = new CommandSpec("env", "/bin/sh", ["-c", "echo $FOO; read x || echo eof"])
        {
            Environment = new Dictionary<string, string> { ["FOO"] = "bar" },
        };

        var result = await runner.RunAsync(spec, lines.Add, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["bar", "eof"], lines);
    }

    [Fact]
    public async Task Process_runner_kills_on_timeout()
    {
        if (OperatingSystem.IsWindows())
            return;
        var runner = new ProcessCommandRunner("/usr/bin:/bin");

        var result = await runner.RunAsync(new CommandSpec("sleep", "/bin/sleep", ["30"]), _ => { },
            TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(result.Duration < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Process_runner_reports_missing_executable()
    {
        var runner = new ProcessCommandRunner("");
        var result = await runner.RunAsync(new CommandSpec("x", Path.Combine(_dir, "does-not-exist"), []), _ => { },
            TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Finds_enclosing_app_bundle()
    {
        Assert.Equal("/Applications/PkgCleaner.app",
            PkgCleaner.Core.Platform.LaunchAtLogin.FindEnclosingAppBundle("/Applications/PkgCleaner.app/Contents/MacOS/PkgCleaner"));
        Assert.Null(PkgCleaner.Core.Platform.LaunchAtLogin.FindEnclosingAppBundle("/usr/local/bin/PkgCleaner"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
