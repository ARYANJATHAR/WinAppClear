using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinAppClear.Core.Util;

/// <summary>Reads and writes small JSON files atomically (write to temp, then replace).</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object Gate = new();

    public static T Load<T>(string path) where T : new()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(path)) return new T();
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
            }
            catch (Exception ex)
            {
                Log.Error($"Could not read {path}", ex);
                return new T();
            }
        }
    }

    public static void Save<T>(string path, T value)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
    }
}
