using System.Runtime.InteropServices;
using Avalonia;
using Sweepbox.Core.Engine;
using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;
using Sweepbox.Desktop.Services;

namespace Sweepbox.Desktop;

internal static partial class Program
{
    private static string Usage => $"""
        Sweepbox - keeps {Core.Managers.PackageManager.JoinNames(Core.Managers.PackageManager.ForThisOS)} up to date and tidy.

        Usage:
          Sweepbox              Start the menu bar / tray app
          Sweepbox --dry-run    Show which commands a run would execute, then exit
          Sweepbox --run        Run maintenance once without UI, then exit (exit code 0 = success)
          Sweepbox --help       Show this help
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.Default();

        if (args.Length > 0 && args[0] is "--help" or "-h" or "--dry-run" or "--run")
        {
            AttachParentConsole();
            return args[0] switch
            {
                "--dry-run" => DryRun(paths),
                "--run" => WithInstanceLock(paths, () => HeadlessRun(paths)),
                _ => Help(),
            };
        }

        return WithInstanceLock(paths, () =>
        {
            App.Paths = paths;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            return 0;
        });
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .WithInterFont()
            .LogToTrace();

    /// <summary>Single instance: hold an exclusive lock on a file for the lifetime of the process.</summary>
    private static int WithInstanceLock(AppPaths paths, Func<int> body)
    {
        FileStream instanceLock;
        try
        {
            instanceLock = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Sweepbox is already running.");
            return 2;
        }
        using (instanceLock)
            return body();
    }

    private static int Help()
    {
        Console.WriteLine(Usage);
        return 0;
    }

    private static int DryRun(AppPaths paths)
    {
        var settings = new SettingsStore(paths.SettingsFile).Current;
        var runner = new ProcessCommandRunner(ShellEnvironment.ResolvePathAsync(settings.ExtraPath).GetAwaiter().GetResult());
        Console.WriteLine($"Settings: {paths.SettingsFile}");
        Console.WriteLine($"Schedule: {(settings.ScheduleEnabled ? $"every {settings.IntervalHours} h" : "off")}");
        foreach (var manager in Core.Managers.PackageManager.ForThisOS)
        {
            Console.WriteLine();
            var exe = runner.ResolveExecutable(manager.ExecutableName);
            var state = !manager.IsEnabled(settings) ? "disabled" : exe is null ? "not installed" : exe;
            Console.WriteLine($"{manager.DisplayName}: {state}");
            if (exe is null || !manager.IsEnabled(settings))
                continue;
            foreach (var step in manager.BuildSteps(settings, exe))
                Console.WriteLine($"  {step.Description,-36} {step.CommandLine}{(step.Elevate ? "   (elevated)" : "")}");
        }
        return 0;
    }

    private static int HeadlessRun(AppPaths paths)
    {
        var services = new AppServices(paths);
        services.Engine.Output += Console.WriteLine;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            services.Engine.Cancel();
        };
        var report = services.Engine.RunAsync(RunTrigger.Manual).GetAwaiter().GetResult();
        return report?.Outcome is Outcome.Succeeded or Outcome.Skipped ? 0 : 1;
    }

    /// <summary>The Windows build is a GUI exe; attach to the calling terminal so CLI output is visible.</summary>
    private static void AttachParentConsole()
    {
        if (OperatingSystem.IsWindows())
            AttachConsole(-1);
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
