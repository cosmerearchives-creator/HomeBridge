using System;
using System.IO;
using System.Text.Json;

namespace HomeBridge.Desktop;
public sealed class AppSettings
{
    public string DisplayName { get; set; } = Environment.MachineName;
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeBridge", "settings.json");
    public static AppSettings Load()
    {
        try {
            if (File.Exists(PathName))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathName)) ?? new AppSettings();
        } catch (IOException) {} catch (JsonException) {} catch (UnauthorizedAccessException) {}
        return new AppSettings();
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var tmp = PathName + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, PathName, true);
    }
}
