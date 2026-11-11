using Sweepbox.Core.Engine;

namespace Sweepbox.Desktop.Services;

public static class Format
{
    public static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        var today = DateTime.Today;
        var day = local.Date == today ? "Today"
            : local.Date == today.AddDays(-1) ? "Yesterday"
            : local.Date == today.AddDays(1) ? "Tomorrow"
            : local.ToString("ddd d MMM");
        return $"{day} {local:HH:mm}";
    }

    public static string Relative(DateTimeOffset time)
    {
        var delta = time - DateTimeOffset.Now;
        if (delta <= TimeSpan.FromMinutes(1))
            return "any moment";
        if (delta < TimeSpan.FromHours(1))
            return $"in {(int)Math.Ceiling(delta.TotalMinutes)} min ({time.ToLocalTime():HH:mm})";
        if (delta < TimeSpan.FromHours(20))
            return $"in {delta.TotalHours:0.#} h ({time.ToLocalTime():HH:mm})";
        return When(time);
    }

    public static string Outcome(Outcome outcome) => outcome switch
    {
        Core.Engine.Outcome.Succeeded => "OK",
        Core.Engine.Outcome.Failed => "Failed",
        Core.Engine.Outcome.Skipped => "Nothing to do",
        _ => "Cancelled",
    };

    /// <summary>"Space freed: 3.4 GB in 12 runs", or null if no run measured cleanup.</summary>
    public static string? TotalFreed(IReadOnlyList<RunReport> runs)
    {
        var measured = runs.Where(r => r.BytesFreed is not null).ToList();
        if (measured.Count == 0)
            return null;
        var total = measured.Sum(r => r.BytesFreed!.Value);
        return $"Space freed: {Core.Running.DiskUsage.Format(total)} in {measured.Count} run{(measured.Count == 1 ? "" : "s")}";
    }

    public static string Duration(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{span.TotalSeconds:0}s";
}
