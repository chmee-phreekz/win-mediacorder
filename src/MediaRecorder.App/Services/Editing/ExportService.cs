using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaRecorder.Core.Editing;
using MediaRecorder.Core.Encoding;
using Windows.Media.Editing;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace MediaRecorder.App.Services.Editing;

/// <summary>
/// Rendert die Timeline mit den gewählten Codec-Einstellungen in eine Datei.
/// Robustheit: es wird zuerst in eine temporäre Datei im Zielordner geschrieben und erst nach
/// erfolgreichem Abschluss umbenannt. Ein Abbruch oder Fehler hinterlässt keine halbe Zieldatei.
/// </summary>
public sealed class ExportService
{
    public async Task ExportAsync(Timeline timeline, EncodingSettings settings, string targetPath,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (timeline.IsEmpty)
            throw new InvalidOperationException("Die Timeline ist leer.");
        var errors = settings.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        if (!settings.HasExplicitResolution)
            throw new InvalidOperationException("Für den Export muss eine Auflösung gesetzt sein.");

        var composition = await new CompositionBuilder().BuildAsync(timeline, settings.Width, settings.Height);
        var profile = EncodingProfileFactory.Create(settings);

        var directory = Path.GetDirectoryName(targetPath) ?? throw new ArgumentException("Ungültiger Zielpfad.", nameof(targetPath));
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var tempName = $"{Path.GetFileNameWithoutExtension(targetPath)}.exporting{Path.GetExtension(targetPath)}";
        var temp = await folder.CreateFileAsync(tempName, CreationCollisionOption.ReplaceExisting);

        bool success = false;
        try
        {
            var reason = await composition
                .RenderToFileAsync(temp, MediaTrimmingPreference.Precise, profile)
                .AsTask(cancellationToken, progress);

            if (reason != TranscodeFailureReason.None)
                throw new InvalidOperationException(FailureText(reason));

            await temp.RenameAsync(Path.GetFileName(targetPath), NameCollisionOption.ReplaceExisting);
            success = true;
        }
        finally
        {
            if (!success)
            {
                try { await temp.DeleteAsync(StorageDeleteOption.PermanentDelete); }
                catch { /* Datei evtl. noch gesperrt; bleibt als .exporting liegen */ }
            }
        }
    }

    private static string FailureText(TranscodeFailureReason reason) => reason switch
    {
        TranscodeFailureReason.CodecNotFound => "Der gewählte Codec ist auf diesem System nicht verfügbar (z. B. HEVC-Erweiterung aus dem Microsoft Store fehlt).",
        TranscodeFailureReason.InvalidProfile => "Die Kombination aus Container, Codec und Einstellungen wird nicht unterstützt.",
        _ => $"Export fehlgeschlagen ({reason}).",
    };
}
