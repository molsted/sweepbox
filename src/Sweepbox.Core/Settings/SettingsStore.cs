namespace Sweepbox.Core.Settings;

public sealed class SettingsStore(string path)
{
    private readonly Lock _lock = new();
    private AppSettings? _current;

    public event Action<AppSettings>? Changed;

    /// <summary>
    /// True if a settings file exists but couldn't be read (corrupt or unreadable), so defaults are in use.
    /// Callers shouldn't act on those defaults as if the user had chosen them (e.g. remove the login item).
    /// </summary>
    public bool LoadFailed { get { _ = Current; return _loadFailed; } }

    private bool _loadFailed;

    /// <summary>A copy of the current settings; mutate it and pass it to <see cref="Save"/>.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                if (_current is null)
                {
                    _current = (JsonFile.Read<AppSettings>(path, out var failed) ?? new AppSettings()).Normalize();
                    _loadFailed = failed;
                }
                return _current.Clone();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        var copy = settings.Clone().Normalize();
        lock (_lock)
        {
            JsonFile.Write(path, copy);
            _current = copy;
            _loadFailed = false; // the user saved: these are now their settings
        }
        Changed?.Invoke(copy.Clone());
    }
}
