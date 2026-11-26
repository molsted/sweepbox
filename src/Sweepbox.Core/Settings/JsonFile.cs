using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sweepbox.Core.Settings;

/// <summary>Why <see cref="JsonFile.Read{T}(string, out ReadFailure)"/> returned null for an existing file.</summary>
internal enum ReadFailure { None, Corrupt, Unreadable }

internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Read<T>(string path) where T : class => Read<T>(path, out ReadFailure _);

    /// <summary>
    /// Reads <paramref name="path"/>; null if it's missing. A corrupt or unreadable file also gives null, with
    /// <paramref name="failure"/> saying why: a corrupt one is kept as <c>.corrupt</c>, and neither ever throws.
    /// </summary>
    public static T? Read<T>(string path, out ReadFailure failure) where T : class
    {
        failure = ReadFailure.None;
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (JsonException ex)
        {
            failure = ReadFailure.Corrupt;
            Trace.WriteLine($"{path} is not valid JSON ({ex.Message}); kept a copy as {Path.GetFileName(path)}.corrupt");
            try { File.Copy(path, path + ".corrupt", overwrite: true); }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException) { }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ReadFailure.Unreadable;
            Trace.WriteLine($"Could not read {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes atomically (temp file + move) so a crash never leaves a half-written file.</summary>
    public static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}
