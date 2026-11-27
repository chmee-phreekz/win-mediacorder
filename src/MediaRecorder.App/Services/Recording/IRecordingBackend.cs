using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MediaRecorder.Core.Devices;
using MediaRecorder.Core.Encoding;
using MediaRecorder.Core.Recording;
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

    Task StartRecordingAsync(string filePath, EncodingSettings settings);
    Task PauseAsync();
    Task ResumeAsync();

    /// <summary>Beendet und finalisiert die Datei. Gibt den Pfad zurück.</summary>
    Task<string?> StopRecordingAsync();
}

public sealed record RecordingFault(string Message, Exception? Exception, string? SavedFilePath);
