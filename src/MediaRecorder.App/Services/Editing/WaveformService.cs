using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaRecorder.Core.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace MediaRecorder.App.Services.Editing;

/// <summary>
/// Erzeugt im Hintergrund die Wellenform jeder importierten Datei mit Ton.
/// Weg: Media Foundation (MediaTranscoder) dekodiert die Tonspur schneller als Echtzeit in ein
/// 8-kHz-Mono-WAV im Temp-Ordner, daraus wird die Hüllkurve berechnet und die Datei wieder gelöscht.
/// Dateien werden nacheinander verarbeitet, damit der Import nicht ausgebremst wird.
/// </summary>
public sealed class WaveformService : IDisposable
{
    public const int PeaksPerSecond = 100;

    private readonly ConcurrentDictionary<Guid, Waveform> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Eine Wellenform ist fertig (Aufruf auf einem beliebigen Thread).</summary>
    public event EventHandler<Guid>? WaveformReady;

    public Waveform? Get(Guid assetId) => _cache.TryGetValue(assetId, out var w) ? w : null;

    public void Forget(Guid assetId) => _cache.TryRemove(assetId, out _);

    /// <summary>Startet die Berechnung; Fehler werden geschluckt (die Wellenform ist nur Anzeige).</summary>
    public async Task LoadAsync(MediaAsset asset)
    {
        if (!asset.HasAudio || _cache.ContainsKey(asset.Id)) return;
        var token = _cts.Token;
        try
        {
            await _gate.WaitAsync(token);
        }
        catch (OperationCanceledException) { return; }

        string? tempPath = null;
        try
        {
            var source = await StorageFile.GetFileFromPathAsync(asset.Path);
            var dir = Path.Combine(Path.GetTempPath(), "MediaRecorder");
            Directory.CreateDirectory(dir);
            var folder = await StorageFolder.GetFolderFromPathAsync(dir);
            var target = await folder.CreateFileAsync($"wave_{asset.Id:N}.wav", CreationCollisionOption.ReplaceExisting);
            tempPath = target.Path;

            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            profile.Audio = AudioEncodingProperties.CreatePcm(8000, 1, 16);
            profile.Video = null;

            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareFileTranscodeAsync(source, target, profile);
            if (!prepared.CanTranscode) return;
            await prepared.TranscodeAsync().AsTask(token);

            var waveform = await Task.Run(() =>
            {
                using var stream = File.OpenRead(target.Path);
                return Waveform.FromWav(stream, PeaksPerSecond);
            }, token);

            _cache[asset.Id] = waveform;
            WaveformReady?.Invoke(this, asset.Id);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Wellenform für {asset.Path} fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { /* Temp-Datei, unkritisch */ }
            }
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _cts.Cancel(); // nicht Dispose: laufende Aufrufe lesen das Token noch
    }
}
