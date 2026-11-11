using PkgCleaner.Core.Engine;

namespace PkgCleaner.Desktop.Services;

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

    public static string Duration(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{span.TotalSeconds:0}s";
}
