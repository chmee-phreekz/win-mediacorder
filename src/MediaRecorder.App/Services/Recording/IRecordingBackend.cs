using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MediaRecorder.Core.Audio;
using MediaRecorder.Core.Devices;
using MediaRecorder.Core.Encoding;
using MediaRecorder.Core.Recording;
using MediaRecorder.Core.Video;
using Windows.Graphics.Imaging;
using Windows.Media.Playback;

namespace MediaRecorder.App.Services.Recording;

/// <summary>
/// Abstraktion des Aufnahme-Backends. Implementierung: MediaCapture (Media Foundation).
/// Die Schnittstelle trennt die Seite von der Aufnahmetechnik und macht das ViewModel testbar.
/// </summary>
public interface IRecordingBackend : IAsyncDisposable
{
    RecordingState State { get; }

    /// <summary>Wird aus beliebigen Threads ausgelöst, die UI muss auf den UI-Thread wechseln.</summary>
    event EventHandler<RecordingState>? StateChanged;

    /// <summary>Gerät verloren, Encoder-Fehler, Platte voll ... Eine laufende Aufnahme ist dann bereits finalisiert.</summary>
    event EventHandler<RecordingFault>? Faulted;

    /// <summary>Öffnet die Geräte und startet die Vorschau.</summary>
    Task InitializeAsync(string videoDeviceId, string? audioDeviceId);

    /// <summary>Formate des geöffneten Videogeräts (erst nach InitializeAsync).</summary>
    IReadOnlyList<VideoCaptureFormat> SupportedFormats { get; }

    Task SetVideoFormatAsync(VideoCaptureFormat format);

    /// <summary>MediaPlayer mit Live-Vorschau für ein MediaPlayerElement.</summary>
    MediaPlayer? PreviewPlayer { get; }

    /// <summary>Prüft, ob dieses System die gewählten Codecs schreiben kann. Null = ok, sonst Begründung.</summary>
    Task<string?> CheckSupportAsync(EncodingSettings settings);

    /// <summary>Kompressor-Einstellungen; wirken sofort, auch während der Aufnahme.</summary>
    void SetCompressor(CompressorSettings settings);

    /// <summary>True, wenn der Kompressor-Effekt im Aufnahmepfad eingebunden ist.</summary>
    bool IsCompressorActive { get; }

    /// <summary>Grund, falls der Effekt nicht eingebunden werden konnte.</summary>
    string? CompressorError { get; }

    /// <summary>
    /// LUT setzen (lädt die .cube-Datei, wirft bei ungültiger Datei). Wirkt sofort auf Vorschau und,
    /// wenn der Effekt eingebunden ist, auf die Aufnahme.
    /// </summary>
    Task SetLutAsync(LutSettings settings);

    /// <summary>True, wenn der LUT-Effekt im Aufnahmepfad eingebunden ist.</summary>
    bool IsLutEffectActive { get; }
    string? LutEffectError { get; }

    /// <summary>True, solange die Vorschau über die LUT-Bildausgabe statt über den MediaPlayer läuft.</summary>
    bool IsLutPreviewActive { get; }
    event EventHandler<bool>? LutPreviewActiveChanged;

    /// <summary>LUT-Vorschaubild (Hintergrund-Thread); der Empfänger gibt das Bitmap frei.</summary>
    event EventHandler<SoftwareBitmap>? LutPreviewFrame;

    Task StartRecordingAsync(string filePath, EncodingSettings settings);
    Task PauseAsync();
    Task ResumeAsync();

    /// <summary>Beendet und finalisiert die Datei. Gibt den Pfad zurück.</summary>
    Task<string?> StopRecordingAsync();
}

public sealed record RecordingFault(string Message, Exception? Exception, string? SavedFilePath);
