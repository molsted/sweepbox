namespace Sweepbox.Core.Engine;

public enum RunTrigger
{
    Scheduled,
    Manual,
}

public enum Outcome
{
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
}

public sealed class StepReport
{
    public string Description { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public int ExitCode { get; set; }
    public double DurationSeconds { get; set; }
    public Outcome Outcome { get; set; }
    public string? Message { get; set; }
}

public sealed class ManagerReport
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public Outcome Outcome { get; set; }
    public string? Message { get; set; }

    /// <summary>Bytes freed by the cleanup steps; null when no cleanup step ran.</summary>
    public long? BytesFreed { get; set; }

    public List<StepReport> Steps { get; set; } = [];
}

public sealed class RunReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public RunTrigger Trigger { get; set; }
    public string? LogFile { get; set; }
    public List<ManagerReport> Managers { get; set; } = [];

    public Outcome Outcome =>
        Managers.Any(m => m.Outcome == Outcome.Cancelled) ? Outcome.Cancelled
        : Managers.Any(m => m.Outcome == Outcome.Failed) ? Outcome.Failed
        : Managers.All(m => m.Outcome == Outcome.Skipped) ? Outcome.Skipped
        : Outcome.Succeeded;

    /// <summary>Total bytes freed by cleanup steps; null when cleanup was not enabled/run for any manager.</summary>
    public long? BytesFreed => Managers.Any(m => m.BytesFreed is not null) ? Managers.Sum(m => m.BytesFreed ?? 0) : null;

    public string Summary
    {
        get
        {
            var parts = Managers.Select(m => $"{m.DisplayName}: {m.Outcome switch
            {
                Outcome.Succeeded => "OK",
                Outcome.Failed => "failed",
                Outcome.Skipped => "skipped",
                _ => "cancelled",
            }}");
            var summary = string.Join(", ", parts);
            return BytesFreed is { } freed ? $"{summary}. {FreedText(freed)}." : summary;
        }
    }

    /// <summary>"Freed 1.2 GB" / "Nothing to clean up".</summary>
    public static string FreedText(long bytes) =>
        bytes > 0 ? $"Freed {Running.DiskUsage.Format(bytes)}" : "Nothing to clean up";
}
