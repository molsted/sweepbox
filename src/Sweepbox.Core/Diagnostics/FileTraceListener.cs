using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Sweepbox.Core.Diagnostics;

/// <summary>
/// Appends <see cref="Trace"/> output to a log file, one timestamped line per message, so diagnostics
/// from an installed app (failed notifications, update installs, login items) can be read afterwards.
/// When the file passes <c>maxBytes</c> it is moved to <c>&lt;file&gt;.1</c> (replacing the previous copy).
/// Writing never throws: a log that cannot be written is simply lost.
/// </summary>
public sealed class FileTraceListener : TraceListener
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly StringBuilder _pending = new();

    public FileTraceListener(string path, long maxBytes = 1_000_000, TimeProvider? time = null) : base("SweepboxFile")
    {
        _path = path;
        _maxBytes = maxBytes;
        _time = time ?? TimeProvider.System;
    }

    // Trace output arrives from background threads (engine events, native callbacks); every line is
    // written and closed under the lock, so a crash loses nothing that was already reported.
    public override bool IsThreadSafe => true;

    public override void Write(string? message)
    {
        lock (_lock)
            _pending.Append(message);
    }

    public override void WriteLine(string? message)
    {
        lock (_lock)
        {
            _pending.Append(message);
            var timestamp = _time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"{timestamp}  {_pending}{Environment.NewLine}";
            _pending.Clear();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var existing = new FileInfo(_path);
                if (existing.Exists && existing.Length >= _maxBytes)
                    File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere left to report it; dropping the line is the only safe option.
            }
        }
    }
}
