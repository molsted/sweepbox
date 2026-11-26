using System.Diagnostics;
using Sweepbox.Core.Settings;

namespace Sweepbox.Core.Engine;

/// <summary>Keeps the most recent run reports on disk; the latest one drives the schedule.</summary>
public sealed class RunHistoryStore(string path, int maxEntries = 50)
{
    private sealed class HistoryFile
    {
        public List<RunReport> Runs { get; set; } = [];
    }

    private readonly Lock _lock = new();
    private List<RunReport>? _runs;

    public IReadOnlyList<RunReport> Runs
    {
        get { lock (_lock) return [.. Load()]; }
    }

    public RunReport? Latest
    {
        get { lock (_lock) return Load().LastOrDefault(); }
    }

    /// <summary>The newest run that wasn't an uninstall; this is what the schedule and the Overview follow.</summary>
    public RunReport? LatestMaintenance
    {
        get { lock (_lock) return Load().LastOrDefault(r => r.Trigger != RunTrigger.Uninstall); }
    }

    /// <summary>
    /// Records a run. It is kept in memory even if the file can't be written (full or read-only disk),
    /// so the schedule still moves on; the failure goes to Trace instead of ending the run badly.
    /// </summary>
    public void Add(RunReport report)
    {
        lock (_lock)
        {
            var runs = Load();
            runs.Add(report);
            if (runs.Count > maxEntries)
                runs.RemoveRange(0, runs.Count - maxEntries);
            if (_readFailed)
            {
                // The file exists but couldn't be read: keep it as it is rather than replace it with this session's runs.
                Trace.WriteLine($"Not saving run history: {path} couldn't be read when Sweepbox started.");
                return;
            }
            try
            {
                JsonFile.Write(path, new HistoryFile { Runs = runs });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"Could not save run history to {path}: {ex.Message}");
            }
        }
    }

    private bool _readFailed;

    private List<RunReport> Load()
    {
        if (_runs is null)
        {
            _runs = JsonFile.Read<HistoryFile>(path, out ReadFailure failure)?.Runs ?? [];
            // A corrupt file was kept as .corrupt and may be replaced; an unreadable one is left alone.
            _readFailed = failure == ReadFailure.Unreadable;
        }
        return _runs;
    }
}
