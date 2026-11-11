using System.Text.Json;
using System.Text.Json.Serialization;

namespace PkgCleaner.Core.Settings;

internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (JsonException)
        {
            // Corrupt file: keep a copy for inspection and fall back to defaults.
            File.Copy(path, path + ".corrupt", overwrite: true);
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
