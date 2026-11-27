using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaRecorder.Core.Recording;

namespace MediaRecorder.App.Services.Recording;

/// <summary>
/// Speichert die zuletzt gewählten Aufnahme-Einstellungen in %LOCALAPPDATA%\MediaRecorder\record-settings.json.
/// (Unpackaged-App: ApplicationData.Current steht nicht zur Verfügung.)
/// Schreibt atomar über eine Temp-Datei, damit ein Absturz die Einstellungen nicht zerstört.
/// </summary>
public static class RecordingSettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MediaRecorder", "record-settings.json");

    public static RecordingOptions Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<RecordingOptions>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch { /* Beschädigt oder alte Version: mit Standardwerten weitermachen */ }
        return new RecordingOptions();
    }

    public static void Save(RecordingOptions options)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(options, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* Einstellungen sind nicht kritisch */ }
    }
}
