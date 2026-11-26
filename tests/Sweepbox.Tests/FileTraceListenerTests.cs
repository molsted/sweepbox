using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using Sweepbox.Core.Diagnostics;

namespace Sweepbox.Tests;

public sealed class FileTraceListenerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sweepbox-trace-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 26, 12, 34, 56, 789, TimeSpan.Zero));

    private string LogPath => Path.Combine(_dir, "logs", "sweepbox.log");

    [Fact]
    public void Writes_each_line_with_a_timestamp_and_creates_the_folder()
    {
        using var listener = new FileTraceListener(LogPath, time: _time);

        listener.WriteLine("Notification failed: denied");

        Assert.Equal(["2026-11-26 12:34:56.789  Notification failed: denied"], File.ReadAllLines(LogPath));
    }

    [Fact]
    public void Partial_writes_end_up_on_one_line()
    {
        using var listener = new FileTraceListener(LogPath, time: _time);

        listener.Write("Update: ");
        listener.Write("could not ");
        listener.WriteLine("extract");

        Assert.Equal(["2026-11-26 12:34:56.789  Update: could not extract"], File.ReadAllLines(LogPath));
    }

    [Fact]
    public void Rotates_past_the_size_limit_and_keeps_one_old_copy()
    {
        using var listener = new FileTraceListener(LogPath, maxBytes: 200, time: _time);

        for (var i = 0; i < 30; i++)
            listener.WriteLine($"line {i:00} padding padding");

        var files = Directory.GetFiles(Path.GetDirectoryName(LogPath)!).Select(f => Path.GetFileName(f)).Order().ToArray();
        Assert.Equal(["sweepbox.log", "sweepbox.log.1"], files);
        Assert.True(new FileInfo(LogPath).Length <= 200 + 100); // at most one line past the limit
        Assert.EndsWith("line 29 padding padding", File.ReadAllLines(LogPath)[^1]);
    }

    [Fact]
    public void Concurrent_writes_keep_every_line_whole()
    {
        using var listener = new FileTraceListener(LogPath, time: _time);

        Parallel.For(0, 200, i => listener.WriteLine($"message {i}"));

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(200, lines.Length);
        Assert.All(lines, l => Assert.Matches(new Regex(@"^2026-11-26 12:34:56\.789  message \d+$"), l));
        Assert.Equal(200, lines.Distinct().Count());
    }

    [Fact]
    public void An_unwritable_path_never_throws()
    {
        var blocker = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocker, "");
        using var listener = new FileTraceListener(Path.Combine(blocker, "sweepbox.log"), time: _time);

        var ex = Record.Exception(() => listener.WriteLine("lost, but no crash"));

        Assert.Null(ex);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
