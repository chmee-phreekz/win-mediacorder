using System;
using System.IO;
using MediaRecorder.Core.Audio;
using MediaRecorder.Core.Encoding;
using MediaRecorder.Core.Video;

namespace MediaRecorder.Core.Recording;

public enum RecordingState
{
    Idle,
    Initializing,
    Previewing,
    Recording,
    Paused,
    Stopping,
    Faulted
}

/// <summary>Alles, was für eine Aufnahme gewählt wurde. Wird als JSON gespeichert und beim Start wiederhergestellt.</summary>
public sealed class RecordingOptions
{
    public string? VideoDeviceId { get; set; }
    public string? AudioDeviceId { get; set; }

    /// <summary>Null = Gerät wählt selbst (meist höchste Auflösung).</summary>
    public string? VideoFormatKey { get; set; }

    public EncodingSettings Encoding { get; set; } = new();

    /// <summary>Kompressor für den Audioeingang (wirkt auf Aufnahme und VU-Meter).</summary>
    public CompressorSettings Compressor { get; set; } = new();

    /// <summary>LUT für das Videosignal (wirkt auf Aufnahme und Vorschau).</summary>
    public LutSettings Lut { get; set; } = new();

    public string OutputFolder { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    /// <summary>Platzhalter: {date} {time} {device}.</summary>
    public string FileNamePattern { get; set; } = "Aufnahme_{date}_{time}";

    /// <summary>Unter diesem freien Speicher (MB) wird die Aufnahme sauber beendet, bevor die Datei beschädigt wird.</summary>
    public long MinimumFreeSpaceMb { get; set; } = 1024;

    public string BuildFilePath(string extension, string? deviceName = null, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        var name = FileNamePattern
            .Replace("{date}", t.ToString("yyyy-MM-dd"))
            .Replace("{time}", t.ToString("HH-mm-ss"))
            .Replace("{device}", deviceName ?? "Gerät");

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var path = Path.Combine(OutputFolder, name + ext);

        // Nie eine bestehende Datei überschreiben.
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(OutputFolder, $"{name}_{i}{ext}");

        return path;
    }
}

public static class VideoFormatKey
{
    public static string For(Devices.VideoCaptureFormat f) =>
        $"{f.Width}x{f.Height}@{f.FrameRate:0.###}/{f.Subtype}";
}
