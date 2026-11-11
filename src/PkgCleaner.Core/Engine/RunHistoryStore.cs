using PkgCleaner.Core.Settings;

namespace PkgCleaner.Core.Engine;

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

    public void Add(RunReport report)
    {
        lock (_lock)
        {
            var runs = Load();
            runs.Add(report);
            if (runs.Count > maxEntries)
                runs.RemoveRange(0, runs.Count - maxEntries);
            JsonFile.Write(path, new HistoryFile { Runs = runs });
        }
    }

    private List<RunReport> Load() => _runs ??= JsonFile.Read<HistoryFile>(path)?.Runs ?? [];
}
