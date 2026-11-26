// Renders Sweepbox's real windows off-screen with sample data, for the manual (user-docs/images).
//   dotnet run --project tools/Screenshots -- <output dir>      then: python3 tools/frame_screenshots.py
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sweepbox.Core.Engine;
using Sweepbox.Core.Managers;
using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;
using Sweepbox.Desktop;
using Sweepbox.Desktop.Services;
using Sweepbox.Desktop.ViewModels;
using Sweepbox.Desktop.Views;

var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "user-docs/images/raw");
Directory.CreateDirectory(outDir);
// The app runs with InvariantGlobalization; match it so numbers and dates don't depend on this machine's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .With(new FontManagerOptions { DefaultFamilyName = "avares://Avalonia.Fonts.Inter/Assets#Inter" })
    .SetupWithoutStarting();

// Render as if it were 09:41 today, so relative times ("Next run Tomorrow 08:02") don't depend on when this runs.
Format.Clock = new FixedClock(new DateTimeOffset(DateTime.Today.AddHours(9).AddMinutes(41)));

var macServices = SampleData.Create(windows: false);
var winServices = SampleData.Create(windows: true);
foreach (var (theme, suffix) in new[] { (ThemeVariant.Light, ""), (ThemeVariant.Dark, "-dark") })
{
    Application.Current!.RequestedThemeVariant = theme;

    foreach (var (services, platform) in new[] { (macServices, "macos"), (winServices, "windows") })
    {
        Shot(new SettingsWindow { DataContext = new SettingsViewModel(services.Settings, platform == "windows" ? [new ChocoManager(), new NpmManager()] : services.Engine.Managers) },
            $"settings-{platform}{suffix}", fullHeight: true);
        Shot(new StatusWindow { DataContext = new StatusViewModel(services, () => { }) }, $"status-{platform}{suffix}");
        Shot(new StatusWindow { DataContext = new StatusViewModel(services, () => { }) }, $"status-log-{platform}{suffix}", selectTab: 2);

        var installed = new StatusViewModel(services, () => { });
        installed.Installed.EnsureLoadedAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        // Sizes arrive after the list: wait until measuring has finished.
        for (var waited = Stopwatch.StartNew(); installed.Installed.IsMeasuring && waited.Elapsed < TimeSpan.FromSeconds(10);)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        if (installed.Installed.IsMeasuring)
            throw new InvalidOperationException("Measuring did not finish.");
        foreach (var item in installed.Installed.Groups.SelectMany(g => g.Items).Where(i => i.Package.Name is "zoom" or "typescript" or "gimp" or "yarn").Take(2))
            item.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Shot(new StatusWindow { DataContext = installed }, $"installed-{platform}{suffix}", selectTab: 1);
    }
}
Console.WriteLine($"Screenshots written to {outDir}");
return 0;

void Shot(Window window, string name, bool fullHeight = false, int selectTab = -1)
{
    if (fullHeight)
        window.Height = 2000; // measure everything, then shrink to the content below
    window.Show();
    if (selectTab >= 0)
        window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = selectTab;
    Dispatcher.UIThread.RunJobs();
    window.UpdateLayout();

    if (fullHeight)
    {
        // Height = header + footer + full scroll content, so the manual shows every option.
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
        var content = (Control)scroll.Content!;
        window.Height = Math.Ceiling(window.Bounds.Height - scroll.Bounds.Height + content.DesiredSize.Height);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    var size = window.Bounds.Size;
    using var bitmap = new RenderTargetBitmap(new PixelSize((int)(size.Width * 2), (int)(size.Height * 2)), new Vector(192, 192));
    bitmap.Render(window);
    bitmap.Save(Path.Combine(outDir, name + ".png"), PngBitmapEncoderOptions.Default);
    window.Close();
}

/// <summary>History, settings and a log that look like a few days of real use.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
}

internal static class SampleData
{
    public static AppServices Create(bool windows)
    {
        var paths = new AppPaths(Directory.CreateTempSubdirectory("sweepbox-screens-").FullName);
        // History must exist before AppServices is created: the engine reads the last run on startup.
        var history = new RunHistoryStore(paths.HistoryFile);
        var today = DateTimeOffset.Now.Date;

        var runs = new (int daysAgo, int hour, bool brewFailed, long brewFreed, long npmFreed, RunTrigger trigger)[]
        {
            (6, 9, false, 412_000_000, 38_000_000, RunTrigger.Scheduled),
            (5, 9, false, 96_000_000, 4_100_000, RunTrigger.Scheduled),
            (4, 9, true, 0, 2_000_000, RunTrigger.Scheduled),
            (3, 9, false, 1_204_000_000, 61_000_000, RunTrigger.Scheduled),
            (2, 9, false, 0, 0, RunTrigger.Scheduled),
            (1, 9, false, 233_000_000, 12_500_000, RunTrigger.Scheduled),
            (0, 8, false, 1_840_000_000, 212_000_000, RunTrigger.Manual),
        };

        foreach (var r in runs)
        {
            var start = new DateTimeOffset(today.AddDays(-r.daysAgo).AddHours(r.hour).AddMinutes(2));
            var report = new RunReport { StartedAt = start, FinishedAt = start.AddSeconds(r.brewFailed ? 214 : 167), Trigger = r.trigger };
            report.Managers.Add(windows ? Choco(r.brewFailed, r.brewFreed / 3) : Brew(r.brewFailed, r.brewFreed));
            report.Managers.Add(Npm(r.npmFreed));
            if (r.daysAgo == 0)
            {
                report.LogFile = Path.Combine(paths.LogDirectory, $"run-{start:yyyyMMdd-HHmmss}.log");
                File.WriteAllText(report.LogFile, windows ? SampleLogWindows(start) : SampleLog(start));
            }
            history.Add(report);
        }

        var managers = windows ? new PackageManager[] { new SampleManager(new ChocoManager(), anyOS: true), new SampleManager(new NpmManager()) }
            : [new SampleManager(new BrewManager()), new SampleManager(new NpmManager())];
        return new AppServices(paths, managers, _ => Task.FromResult<ICommandRunner>(new FakeRunner(windows)));
    }

    private const long MB = 1_000_000, GB = 1_000_000_000;

    /// <summary>Fixed sample sizes: exact, estimated (IsEstimate) and unknown (null), as the real managers report them.</summary>
    private static readonly Dictionary<(string Manager, string Name), PackageSize?> Sizes = new()
    {
        [("brew", "google-chrome")] = new(1_190_000_000, false),
        [("brew", "visual-studio-code")] = new(412 * MB, false),
        [("brew", "zoom")] = new(Convert.ToInt64(0.86 * GB), true),
        [("brew", "slack")] = new(398 * MB, false),
        [("brew", "gimp")] = new(742 * MB, false),
        [("brew", "vlc")] = new(171 * MB, false),
        [("brew", "ffmpeg")] = new(54_700_000, false),
        [("brew", "git")] = new(54_000_000, false),
        [("brew", "jq")] = new(1_200_000, false),
        [("brew", "node")] = new(98_400_000, false),
        [("brew", "ripgrep")] = new(5_900_000, false),
        [("brew", "sqlite")] = new(9_800_000, false),
        [("brew", "tree")] = new(120_000, false),
        [("brew", "wget")] = new(3_700_000, false),
        [("choco", "7zip")] = null, // no unique match in Apps & features
        [("choco", "git")] = new(397 * MB, true),
        [("choco", "googlechrome")] = new(683 * MB, true),
        [("choco", "nodejs")] = new(112 * MB, true),
        [("choco", "notepadplusplus")] = new(14_400_000, true),
        [("choco", "powertoys")] = new(Convert.ToInt64(1.1 * GB), true),
        [("choco", "vscode")] = new(396 * MB, true),
        [("choco", "vlc")] = new(Convert.ToInt64(0.1 * GB), true),
        [("npm", "typescript")] = new(23_000_000, false),
        [("npm", "yarn")] = new(5_200_000, false),
        [("npm", "serve")] = new(9_100_000, false),
    };

    /// <summary>
    /// Wraps a real manager for the screenshots: forwards everything, but reports the fixed <see cref="Sizes"/>.
    /// With <paramref name="anyOS"/> the Windows screenshots can list Chocolatey on a Mac.
    /// </summary>
    private sealed class SampleManager(PackageManager inner, bool anyOS = false) : PackageManager
    {
        public override string Id => inner.Id;
        public override string DisplayName => inner.DisplayName;
        public override string ShortName => inner.ShortName;
        public override string ExecutableName => inner.ExecutableName;
        public override bool IsSupportedOnThisOS => anyOS || inner.IsSupportedOnThisOS;
        public override bool IsEnabled(AppSettings settings) => inner.IsEnabled(settings);
        public override IReadOnlyList<CommandSpec> BuildSteps(AppSettings settings, string executable) => inner.BuildSteps(settings, executable);
        public override Task<ManagerInventory> ListInstalledAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken) =>
            inner.ListInstalledAsync(runner, executable, cancellationToken);
        public override Task<IReadOnlyDictionary<PackageKey, PackageSize?>> MeasureAsync(ICommandRunner runner, string executable,
            IReadOnlyList<InstalledPackage> packages, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<PackageKey, PackageSize?>>(
                packages.ToDictionary(PackageKey.Of, p => Sizes.GetValueOrDefault((Id, p.Name))));
        public override CommandSpec BuildUninstallStep(AppSettings settings, string executable, InstalledPackage package) =>
            inner.BuildUninstallStep(settings, executable, package);
        public override bool IsProtected(string packageName) => inner.IsProtected(packageName);
    }

    /// <summary>Answers the listing probes with canned output. Never starts a process.</summary>
    private sealed class FakeRunner(bool windows) : ICommandRunner
    {
        public string? ResolveExecutable(string name) => "/fake/" + name;

        public Task<CommandResult> RunAsync(CommandSpec spec, Action<string> onOutput, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (spec.IsProbe)
                foreach (var line in Answer(string.Join(' ', spec.Arguments)))
                    onOutput(line);
            return Task.FromResult(new CommandResult(0, TimeSpan.Zero));
        }

        private string[] Answer(string args) => args switch
        {
            "list --cask --versions" => ["google-chrome 131.0.6778.86", "visual-studio-code 1.106.2", "zoom 6.2.11", "slack 4.41.98", "gimp 3.0.4", "vlc 3.0.21"],
            "leaves --installed-on-request" => ["ffmpeg", "git", "jq", "node", "ripgrep", "sqlite", "tree", "wget"],
            "list --formula --versions" => ["ffmpeg 7.1.1", "git 2.52.1", "jq 1.8.1", "node 25.2.1", "ripgrep 15.1.0", "sqlite 3.51.1", "tree 2.2.1", "wget 1.25.0", "openssl@3 3.6.1"],
            "--version" => ["2.5.1"],
            "list --limit-output" when windows =>
                ["7zip|24.9.0", "git|2.52.1", "git.install|2.52.1", "googlechrome|131.0.6778.86", "nodejs|25.2.1", "notepadplusplus|8.8.1", "powertoys|0.96.1", "vscode|1.106.2", "vlc|3.0.21", "chocolatey|2.5.1"],
            "ls --global --depth=0 --json" => ["{\"dependencies\":{\"npm\":{\"version\":\"11.6.2\"},\"typescript\":{\"version\":\"5.9.3\"},\"yarn\":{\"version\":\"1.22.22\"},\"serve\":{\"version\":\"14.2.5\"}}}"],
            _ => [],
        };
    }

    private static ManagerReport Choco(bool failed, long freed)
    {
        var m = new ManagerReport { Id = "choco", DisplayName = "Chocolatey", Outcome = failed ? Outcome.Failed : Outcome.Succeeded, BytesFreed = freed };
        m.Steps.AddRange([Step("Upgrade all packages", 96, failed), Step("Remove download cache", 3)]);
        if (failed)
            m.Message = "Upgrade all packages: Administrator permission was declined.";
        return m;
    }

    private static string SampleLogWindows(DateTimeOffset t)
    {
        var lines = new[]
        {
            "=== Manual run started ===", "", "--- Chocolatey ---",
            "> Upgrade all packages: choco upgrade all --yes --no-progress --limit-output",
            "  git|2.52.0|2.52.1|false", "  nodejs|25.1.0|25.2.1|false", "  vscode|1.106.0|1.106.2|false",
            "  Chocolatey upgraded 3/3 packages.", "OK (96s, exit 0)",
            "> Remove download cache: choco cache remove --yes", "  Removed 214 cached files.", "OK (3s, exit 0)", "Space freed: 584.2 MB",
            "", "--- npm (global) ---",
            "> Update global packages: npm update --global", "  changed 3 packages in 8s", "OK (9s, exit 0)",
            "> Verify and garbage-collect cache: npm cache verify", "  Cache verified and compressed (~\\AppData\\Local\\npm-cache\\_cacache)",
            "  Content garbage-collected: 1,204 (212000000 bytes)", "OK (5s, exit 0)", "Space freed: 202.2 MB",
            "", "=== Finished: Succeeded in 2.1 min ===",
        };
        var time = t;
        return string.Join(Environment.NewLine, lines.Select(l => $"{(time = time.AddSeconds(2)):HH:mm:ss}  {l}"));
    }

    private static ManagerReport Brew(bool failed, long freed)
    {
        var m = new ManagerReport { Id = "brew", DisplayName = "Homebrew", Outcome = failed ? Outcome.Failed : Outcome.Succeeded };
        m.Steps.AddRange([
            Step("Update Homebrew", 6), Step("Upgrade formulae", 74),
            Step("Upgrade casks", 51, failed), Step("Remove unused dependencies", 4), Step("Clean up", 18),
        ]);
        if (failed)
            m.Message = "Upgrade casks: a cask needs an administrator password";
        m.BytesFreed = freed;
        return m;
    }

    private static ManagerReport Npm(long freed)
    {
        var m = new ManagerReport { Id = "npm", DisplayName = "npm (global)", Outcome = Outcome.Succeeded, BytesFreed = freed };
        m.Steps.AddRange([Step("Update global packages", 9), Step("Verify and garbage-collect cache", 5)]);
        return m;
    }

    private static StepReport Step(string description, double seconds, bool failed = false) => new()
    {
        Description = description, DurationSeconds = seconds,
        Outcome = failed ? Outcome.Failed : Outcome.Succeeded, ExitCode = failed ? 1 : 0,
    };

    private static string SampleLog(DateTimeOffset t)
    {
        var lines = new[]
        {
            "=== Manual run started ===", "", "--- Homebrew ---",
            "> Update Homebrew: brew update", "  Updated 2 taps (homebrew/core and homebrew/cask).", "  ==> New Formulae", "  uv-build", "OK (6s, exit 0)",
            "> Upgrade formulae: brew upgrade --formula", "  ==> Upgrading 4 outdated packages:",
            "  node 25.1.0 -> 25.2.1", "  git 2.52.0 -> 2.52.1", "  openssl@3 3.6.0 -> 3.6.1", "  sqlite 3.51.0 -> 3.51.1",
            "  ==> Pouring node--25.2.1.arm64_tahoe.bottle.tar.gz", "  🍺  /opt/homebrew/Cellar/node/25.2.1: 2,621 files, 98.4MB", "OK (74s, exit 0)",
            "> Upgrade casks: brew upgrade --cask", "  ==> Upgrading 1 outdated package:", "  visual-studio-code 1.106.0 -> 1.106.2", "OK (51s, exit 0)",
            "> Remove unused dependencies: brew autoremove", "  ==> Autoremoving 2 unneeded formulae:", "  icu4c@76", "  libuv", "OK (4s, exit 0)",
            "> Clean up: brew cleanup", "  Removing: /opt/homebrew/Cellar/node/25.1.0... (2,618 files, 98.2MB)",
            "  Removing: ~/Library/Caches/Homebrew/visual-studio-code--1.106.0.zip... (154.9MB)",
            "  ==> This operation has freed approximately 1.7GB of disk space.", "OK (18s, exit 0)", "Space freed: 1.8 GB",
            "", "--- npm (global) ---",
            "> Update global packages: npm update --global", "  changed 3 packages in 8s", "OK (9s, exit 0)",
            "> Verify and garbage-collect cache: npm cache verify", "  Cache verified and compressed (~/.npm/_cacache)",
            "  Content garbage-collected: 1,204 (212000000 bytes)", "OK (5s, exit 0)", "Space freed: 212 MB",
            "", "=== Finished: Succeeded in 2.8 min ===",
        };
        var time = t;
        return string.Join(Environment.NewLine, lines.Select(l => $"{(time = time.AddSeconds(2)):HH:mm:ss}  {l}"));
    }
}

