using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaRecorder.Core.Audio;
using MediaRecorder.Core.Devices;
using MediaRecorder.Core.Encoding;
using MediaRecorder.Core.Recording;
using MediaRecorder.Core.Video;
using Windows.Graphics.Imaging;
using MediaRecorder.App.Services;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Foundation.Collections;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;

namespace MediaRecorder.App.Services.Recording;

/// <summary>
/// Aufnahme über Windows.Media.Capture (Media Foundation, Hardware-Encoder wo vorhanden).
///
/// Robustheit:
/// - Alle Zustandswechsel laufen durch ein Semaphore, Doppelklicks oder Start während Stop sind harmlos.
/// - MediaCapture.Failed (Gerät abgezogen, Treiberfehler) finalisiert die laufende Datei so weit möglich und meldet Faulted.
/// - RecordLimitationExceeded wird abgefangen.
/// - Freier Speicher wird alle 2 s geprüft; unter der Schwelle wird sauber gestoppt, damit die MP4 lesbar bleibt.
/// - Bestehende Dateien werden nie überschrieben.
/// </summary>
public sealed class MediaCaptureRecordingBackend : IRecordingBackend
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaCapture? _capture;
    private MediaFrameSource? _previewSource;
    private MediaPlayer? _previewPlayer;
    private MediaSource? _previewMediaSource;
    private LowLagMediaRecording? _recording;
    private string? _currentFile;
    private Timer? _diskTimer;
    private bool _hasAudio;
    private List<(VideoCaptureFormat Format, VideoEncodingProperties Props)> _formats = new();
    private VideoEncodingProperties? _currentProps;
    private RecordingState _state = RecordingState.Idle;

    public long MinimumFreeSpaceMb { get; set; } = 1024;

    // ---------------------------------------------------------------- Kompressor

    /// <summary>Name der registrierungsfreien WinRT-Klasse (app.manifest).</summary>
    private const string CompressorEffectClass = EffectRegistration.CompressorClassId;

    private CompressorSettings _compressorSettings = new();
    private PropertySet? _compressorProps;
    private int _compressorVersion;

    public bool IsCompressorActive { get; private set; }
    public string? CompressorError { get; private set; }

    public void SetCompressor(CompressorSettings settings)
    {
        _compressorSettings = settings.Clamped();
        WriteCompressorProps();
    }

    /// <summary>Überträgt die Werte in das PropertySet, das der Effekt bei jedem Audiopuffer prüft.</summary>
    private void WriteCompressorProps()
    {
        var p = _compressorProps;
        if (p is null) return;
        var s = _compressorSettings;
        p["Enabled"] = s.Enabled;
        p["ThresholdDb"] = s.ThresholdDb;
        p["Ratio"] = s.Ratio;
        p["GainDb"] = s.GainDb;
        p["Version"] = System.Threading.Interlocked.Increment(ref _compressorVersion);
    }

    // ---------------------------------------------------------------- LUT

    private LutSettings _lutSettings = new();
    private BgraLutTable? _previewTable;
    private PropertySet? _lutProps;
    private int _lutVersion;
    private LutPreviewRenderer? _lutPreview;

    public bool IsLutEffectActive { get; private set; }
    public string? LutEffectError { get; private set; }
    public bool IsLutPreviewActive => _lutPreview is not null;
    public event EventHandler<bool>? LutPreviewActiveChanged;
    public event EventHandler<SoftwareBitmap>? LutPreviewFrame;

    public async Task SetLutAsync(LutSettings settings)
    {
        BgraLutTable? table = null;
        if (settings.Enabled && !string.IsNullOrEmpty(settings.Path))
        {
            // Datei prüfen und Vorschau-Tabelle bauen, bevor irgendetwas umgeschaltet wird.
            table = await Task.Run(() => new BgraLutTable(CubeLut.Load(settings.Path!)));
        }

        await _gate.WaitAsync();
        try
        {
            _lutSettings = settings;
            _previewTable = table;
            WriteLutProps();
            await UpdateLutPreviewAsync();
        }
        finally { _gate.Release(); }
    }

    private void WriteLutProps()
    {
        var p = _lutProps;
        if (p is null) return;
        p["Enabled"] = _lutSettings.Enabled && _previewTable is not null;
        p["LutPath"] = _lutSettings.Path ?? "";
        p["Version"] = Interlocked.Increment(ref _lutVersion);
    }

    /// <summary>Muss unter _gate laufen.</summary>
    private async Task UpdateLutPreviewAsync()
    {
        bool want = _previewTable is not null && _capture is not null && _previewSource is not null;
        if (want && _lutPreview is not null)
        {
            _lutPreview.SetTable(_previewTable!);
            return;
        }
        if (!want)
        {
            await StopLutPreviewAsync();
            return;
        }

        try
        {
            var props = _currentProps;
            var renderer = await LutPreviewRenderer.StartAsync(_capture!, _previewSource!,
                props?.Width ?? 0, props?.Height ?? 0, _previewTable!);
            renderer.FrameReady += (_, bmp) => LutPreviewFrame?.Invoke(this, bmp);
            _lutPreview = renderer;
            try { _previewPlayer?.Pause(); } catch { } // MediaPlayer-Vorschau ruht, solange die LUT-Vorschau läuft
            LutPreviewActiveChanged?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            // Vorschau ohne LUT ist besser als keine Vorschau.
            ErrorReporter.Log(ex, "LUT-Vorschau starten");
            LutEffectError ??= "LUT-Vorschau nicht verfügbar: " + ex.Message;
        }
    }

    /// <summary>Muss unter _gate laufen.</summary>
    private async Task StopLutPreviewAsync()
    {
        var r = _lutPreview;
        if (r is null) return;
        _lutPreview = null;
        try { await r.DisposeAsync(); } catch { }
        try { if (_previewPlayer?.Source is not null) _previewPlayer.Play(); } catch { }
        LutPreviewActiveChanged?.Invoke(this, false);
    }

    private async Task AttachLutAsync()
    {
        IsLutEffectActive = false;
        LutEffectError = null;
        try
        {
            _lutProps = new PropertySet();
            WriteLutProps();
            await _capture!.AddVideoEffectAsync(new VideoEffectDefinition(EffectRegistration.LutClassId, _lutProps), MediaStreamType.VideoRecord);
            IsLutEffectActive = true;
        }
        catch (Exception ex)
        {
            _lutProps = null;
            LutEffectError = ex.Message;
            ErrorReporter.Log(ex, "LUT-Effekt einbinden");
        }
    }

    private async Task AttachCompressorAsync()
    {
        IsCompressorActive = false;
        CompressorError = null;
        try
        {
            _compressorProps = new PropertySet();
            WriteCompressorProps();
            await _capture!.AddAudioEffectAsync(new AudioEffectDefinition(CompressorEffectClass, _compressorProps));
            IsCompressorActive = true;
        }
        catch (Exception ex)
        {
            // Aufnahme bleibt möglich, nur ohne Kompressor. Häufigste Ursache: WinRT.Host.dll fehlt im Ausgabeordner.
            _compressorProps = null;
            CompressorError = ex.Message;
            ErrorReporter.Log(ex, "Kompressor-Effekt einbinden");
        }
    }

    public RecordingState State => _state;
    public event EventHandler<RecordingState>? StateChanged;
    public event EventHandler<RecordingFault>? Faulted;

    public IReadOnlyList<VideoCaptureFormat> SupportedFormats => _formats.Select(f => f.Format).ToList();
    public MediaPlayer? PreviewPlayer => _previewPlayer;

    private void SetState(RecordingState s)
    {
        if (_state == s) return;
        _state = s;
        StateChanged?.Invoke(this, s);
    }

    // ---------------------------------------------------------------- Initialisierung

    public async Task InitializeAsync(string videoDeviceId, string? audioDeviceId)
    {
        await _gate.WaitAsync();
        try
        {
            await StopInternalAsync(finalizeOnly: false);
            await ReleaseCaptureAsync();
            SetState(RecordingState.Initializing);

            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = videoDeviceId,
                StreamingCaptureMode = audioDeviceId is null
                    ? StreamingCaptureMode.Video
                    : StreamingCaptureMode.AudioAndVideo,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = MediaCaptureMemoryPreference.Auto,
                PhotoCaptureSource = PhotoCaptureSource.Auto,
            };
            if (audioDeviceId is not null)
                settings.AudioDeviceId = audioDeviceId;
            _hasAudio = audioDeviceId is not null;

            var capture = new MediaCapture();
            try
            {
                await capture.InitializeAsync(settings);
            }
            catch (UnauthorizedAccessException ex)
            {
                capture.Dispose();
                throw new InvalidOperationException(
                    "Kein Zugriff auf Kamera oder Mikrofon. Bitte unter Windows-Einstellungen > Datenschutz > Kamera/Mikrofon den Zugriff für Desktop-Apps erlauben.", ex);
            }
            catch (Exception ex) when ((uint)ex.HResult == 0xC00D3704 /* MF_E_HW_MFT_FAILED_START_STREAMING */
                                     || (uint)ex.HResult == 0xC00DABE4 /* MF_E_VIDEO_RECORDING_DEVICE_IN_USE */)
            {
                capture.Dispose();
                throw new InvalidOperationException("Das Videogerät wird gerade von einer anderen Anwendung verwendet.", ex);
            }

            _capture = capture;
            _capture.Failed += OnCaptureFailed;
            _capture.RecordLimitationExceeded += OnRecordLimitationExceeded;

            LoadFormats();
            await AttachLutAsync();
            if (_hasAudio) await AttachCompressorAsync();
            else { IsCompressorActive = false; CompressorError = null; _compressorProps = null; }
            await StartPreviewAsync();
            await UpdateLutPreviewAsync();
            SetState(RecordingState.Previewing);
        }
        catch
        {
            await ReleaseCaptureAsync();
            SetState(RecordingState.Faulted);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LoadFormats()
    {
        var ctrl = _capture!.VideoDeviceController;
        _formats = ctrl.GetAvailableMediaStreamProperties(MediaStreamType.VideoRecord)
            .OfType<VideoEncodingProperties>()
            .Where(p => p.Width > 0 && p.Height > 0 && p.FrameRate.Denominator > 0)
            .Select(p => (Format: new VideoCaptureFormat(p.Width, p.Height,
                                (double)p.FrameRate.Numerator / p.FrameRate.Denominator,
                                NormalizeSubtype(p.Subtype)), Props: p))
            .GroupBy(x => VideoFormatKey.For(x.Format))
            .Select(g => g.First())
            .OrderByDescending(x => x.Format.Width * x.Format.Height)
            .ThenByDescending(x => x.Format.FrameRate)
            .ThenBy(x => x.Format.IsCompressed) // unkomprimiert (NV12/YUY2) bevorzugen
            .ToList();

        _currentProps = ctrl.GetMediaStreamProperties(MediaStreamType.VideoRecord) as VideoEncodingProperties;
    }

    private static string NormalizeSubtype(string s) => s.ToUpperInvariant() switch
    {
        "MJPG" or "MJPEG" => "MJPG",
        var x => x
    };

    public async Task SetVideoFormatAsync(VideoCaptureFormat format)
    {
        await _gate.WaitAsync();
        try
        {
            if (_capture is null) return;
            if (_state is RecordingState.Recording or RecordingState.Paused)
                throw new InvalidOperationException("Das Format kann während der Aufnahme nicht geändert werden.");

            var match = _formats.FirstOrDefault(f => f.Format == format);
            if (match.Props is null) return;

            var ctrl = _capture.VideoDeviceController;
            await ctrl.SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, match.Props);

            // Bei Geräten mit getrenntem Vorschau-Stream die Vorschau möglichst gleich einstellen.
            var preview = ctrl.GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview)
                .OfType<VideoEncodingProperties>()
                .FirstOrDefault(p => p.Width == match.Props.Width && p.Height == match.Props.Height
                                  && p.FrameRate.Numerator * match.Props.FrameRate.Denominator
                                     == match.Props.FrameRate.Numerator * p.FrameRate.Denominator);
            if (preview is not null && ctrl.GetMediaStreamProperties(MediaStreamType.VideoPreview) is not null)
            {
                try { await ctrl.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, preview); }
                catch { /* Vorschau behält ihr Format, Aufnahme ist korrekt eingestellt. */ }
            }

            _currentProps = match.Props;
            await StopLutPreviewAsync();
            await StartPreviewAsync(); // Vorschau neu verbinden, damit sie das neue Format zeigt.
            await UpdateLutPreviewAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- Vorschau

    private Task StartPreviewAsync()
    {
        StopPreview();

        // In WinUI 3 gibt es kein CaptureElement: Vorschau über MediaFrameSource -> MediaSource -> MediaPlayer.
        var sources = _capture!.FrameSources.Values
            .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            .ToList();
        _previewSource = sources.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
                      ?? sources.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
                      ?? sources.FirstOrDefault();
        if (_previewSource is null)
            throw new InvalidOperationException("Das Gerät liefert keinen Farbvideostream.");

        // Ein einziger MediaPlayer für die ganze Lebensdauer: das MediaPlayerElement in der UI hält ihn fest.
        // Würde er beim Gerätewechsel verworfen, während die UI ihn noch anzeigt, stürzt die App nativ ab
        // (0xC000027B, nicht per try/catch abfangbar). Deshalb wird nur die Quelle getauscht.
        _previewPlayer ??= new MediaPlayer
        {
            RealTimePlayback = true,
            AutoPlay = true,
            IsMuted = true, // Kein Audio-Monitoring über Lautsprecher (Rückkopplung).
        };
        _previewMediaSource = MediaSource.CreateFromMediaFrameSource(_previewSource);
        _previewPlayer.Source = _previewMediaSource;
        _previewPlayer.Play();
        return Task.CompletedTask;
    }

    /// <summary>Löst die Vorschau vom Gerät, bevor MediaCapture freigegeben wird. Der MediaPlayer selbst bleibt bestehen.</summary>
    private void StopPreview()
    {
        if (_previewPlayer is not null)
        {
            try { _previewPlayer.Pause(); } catch { }
            try { _previewPlayer.Source = null; } catch { }
        }
        try { _previewMediaSource?.Dispose(); } catch { }
        _previewMediaSource = null;
        _previewSource = null;
    }

    // ---------------------------------------------------------------- Codec-Prüfung

    public async Task<string?> CheckSupportAsync(EncodingSettings settings)
    {
        MediaEncodingProfile profile;
        try
        {
            profile = MediaRecorder.App.Services.EncodingProfileFactory.Create(settings);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if (profile.Video is { } v)
        {
            var guid = SubtypeToGuid(v.Subtype);
            if (guid is not null)
            {
                var encoders = await new Windows.Media.Core.CodecQuery()
                    .FindAllAsync(Windows.Media.Core.CodecKind.Video, Windows.Media.Core.CodecCategory.Encoder, guid);
                if (encoders.Count == 0)
                    return v.Subtype.ToUpperInvariant() switch
                    {
                        "HEVC" or "H265" => "Für HEVC fehlt ein Encoder. Mit GPU-Treibern (Intel/NVIDIA/AMD) oder der Erweiterung „HEVC-Videoerweiterungen“ aus dem Store verfügbar.",
                        "AV1" or "AV01" => "Für AV1 fehlt ein Hardware-Encoder (ab Intel Arc, NVIDIA RTX 40, AMD RX 7000).",
                        _ => $"Kein Encoder für {v.Subtype} auf diesem System gefunden."
                    };
            }
        }
        return null;
    }

    private static string? SubtypeToGuid(string subtype) => subtype.ToUpperInvariant() switch
    {
        "H264" => Windows.Media.Core.CodecSubtypes.VideoFormatH264,
        "HEVC" or "H265" => Windows.Media.Core.CodecSubtypes.VideoFormatHevc,
        "AV1" or "AV01" => "{31305641-0000-0010-8000-00AA00389B71}",
        "VP90" or "VP9" => Windows.Media.Core.CodecSubtypes.VideoFormatVP90,
        _ => null // Unkomprimiert o.ä.: keine Prüfung nötig
    };

    // ---------------------------------------------------------------- Aufnahme

    public async Task StartRecordingAsync(string filePath, EncodingSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            if (_capture is null || _state != RecordingState.Previewing)
                throw new InvalidOperationException("Keine Quelle bereit.");

            var dir = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(dir);
            EnsureFreeSpace(dir);

            var profile = MediaRecorder.App.Services.EncodingProfileFactory.Create(settings);
            ApplySourceDefaults(profile);
            if (!_hasAudio) profile.Audio = null;

            var folder = await StorageFolder.GetFolderFromPathAsync(dir);
            var file = await folder.CreateFileAsync(Path.GetFileName(filePath), CreationCollisionOption.GenerateUniqueName);

            try
            {
                _recording = await _capture.PrepareLowLagRecordToStorageFileAsync(profile, file);
                await _recording.StartAsync();
            }
            catch
            {
                _recording = null;
                try { await file.DeleteAsync(); } catch { }
                throw;
            }

            _currentFile = file.Path;
            _diskTimer = new Timer(_ => _ = CheckDiskAsync(dir), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            SetState(RecordingState.Recording);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Wenn Auflösung/FPS nicht explizit gewählt sind (0), wird die Quelle 1:1 übernommen, nie hochskaliert.</summary>
    private void ApplySourceDefaults(MediaEncodingProfile profile)
    {
        if (profile.Video is not { } v || _currentProps is null) return;
        if (v.Width == 0 || v.Height == 0)
        {
            v.Width = _currentProps.Width;
            v.Height = _currentProps.Height;
        }
        if (v.FrameRate.Numerator == 0 || v.FrameRate.Denominator == 0)
        {
            v.FrameRate.Numerator = _currentProps.FrameRate.Numerator;
            v.FrameRate.Denominator = _currentProps.FrameRate.Denominator;
        }
        if (v.PixelAspectRatio.Numerator == 0)
        {
            v.PixelAspectRatio.Numerator = 1;
            v.PixelAspectRatio.Denominator = 1;
        }
    }

    private void EnsureFreeSpace(string dir)
    {
        var free = FreeMb(dir);
        if (free is not null && free < MinimumFreeSpaceMb)
            throw new IOException($"Zu wenig freier Speicher im Zielordner ({free} MB frei, mindestens {MinimumFreeSpaceMb} MB nötig).");
    }

    private static long? FreeMb(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return null; // Netzlaufwerk: nicht prüfbar
            return new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
        }
        catch { return null; }
    }

    private async Task CheckDiskAsync(string dir)
    {
        try { await CheckDiskCoreAsync(dir); }
        catch (Exception ex) { ErrorReporter.Log(ex, "Speicherprüfung"); }
    }

    private async Task CheckDiskCoreAsync(string dir)
    {
        var free = FreeMb(dir);
        if (free is null || free >= MinimumFreeSpaceMb / 2 /* während der Aufnahme: halbe Schwelle */) return;
        if (_recording is null) return;
        var path = await StopRecordingAsync();
        if (path is null) return; // schon von anderer Stelle gestoppt
        Faulted?.Invoke(this, new RecordingFault(
            $"Aufnahme automatisch beendet: nur noch {free} MB frei. Die Datei wurde gesichert.", null, path));
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_recording is null || _state != RecordingState.Recording) return;
            await _recording.PauseWithResultAsync(MediaCapturePauseBehavior.RetainHardwareResources);
            SetState(RecordingState.Paused);
        }
        finally { _gate.Release(); }
    }

    public async Task ResumeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_recording is null || _state != RecordingState.Paused) return;
            await _recording.ResumeAsync();
            SetState(RecordingState.Recording);
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> StopRecordingAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await StopInternalAsync(finalizeOnly: false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Muss unter _gate laufen.</summary>
    private async Task<string?> StopInternalAsync(bool finalizeOnly)
    {
        _diskTimer?.Dispose();
        _diskTimer = null;

        if (_recording is null) return null;

        var path = _currentFile;
        SetState(RecordingState.Stopping);
        try
        {
            await _recording.StopAsync();
        }
        catch { /* Gerät evtl. weg; FinishAsync versucht trotzdem, die Datei abzuschließen. */ }
        try
        {
            await _recording.FinishAsync();
        }
        catch { }
        _recording = null;
        _currentFile = null;

        if (!finalizeOnly)
            SetState(_capture is not null ? RecordingState.Previewing : RecordingState.Idle);
        return path;
    }

    // ---------------------------------------------------------------- Fehler

    private async void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs e)
    {
        ErrorReporter.Log(null, $"MediaCapture.Failed 0x{e.Code:X8}: {e.Message}");
        string? saved = null;
        await _gate.WaitAsync();
        try
        {
            saved = await StopInternalAsync(finalizeOnly: true);
            await ReleaseCaptureAsync();
            SetState(RecordingState.Faulted);
        }
        catch { }
        finally { _gate.Release(); }

        var msg = (uint)e.Code switch
        {
            0xC00D3EA2 or 0x80070490 => "Das Aufnahmegerät wurde getrennt.",
            0xC00DABE4 => "Das Videogerät wird von einer anderen Anwendung belegt.",
            _ => $"Aufnahmefehler: {e.Message} (0x{e.Code:X8})"
        };
        Faulted?.Invoke(this, new RecordingFault(msg, null, saved));
    }

    private async void OnRecordLimitationExceeded(MediaCapture sender)
    {
        try
        {
            var path = await StopRecordingAsync();
            Faulted?.Invoke(this, new RecordingFault(
                "Windows hat die maximale Aufnahmedauer erreicht. Die Datei wurde gesichert.", null, path));
        }
        catch (Exception ex)
        {
            ErrorReporter.Log(ex, "RecordLimitationExceeded");
        }
    }

    // ---------------------------------------------------------------- Aufräumen

    private async Task ReleaseCaptureAsync()
    {
        await StopLutPreviewAsync();
        StopPreview();
        if (_capture is not null)
        {
            _capture.Failed -= OnCaptureFailed;
            _capture.RecordLimitationExceeded -= OnRecordLimitationExceeded;
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
        _formats.Clear();
        _currentProps = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopInternalAsync(finalizeOnly: true);
            await ReleaseCaptureAsync();
            SetState(RecordingState.Idle);
            // Erst jetzt (App wird beendet) den MediaPlayer selbst freigeben.
            try { _previewPlayer?.Dispose(); } catch { }
            _previewPlayer = null;
        }
        finally { _gate.Release(); }
    }
}
