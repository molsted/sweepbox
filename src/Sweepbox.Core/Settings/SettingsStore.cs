namespace Sweepbox.Core.Settings;

public sealed class SettingsStore(string path)
{
    private readonly Lock _lock = new();
    private AppSettings? _current;

    public event Action<AppSettings>? Changed;

    /// <summary>A copy of the current settings; mutate it and pass it to <see cref="Save"/>.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                _current ??= (JsonFile.Read<AppSettings>(path) ?? new AppSettings()).Normalize();
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
        }
        Changed?.Invoke(copy.Clone());
    }
}
