using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaRecorder.App.Services.Recording;
using MediaRecorder.Core.Devices;
using MediaRecorder.Core.Encoding;
using MediaRecorder.Core.Recording;
using Microsoft.UI.Dispatching;
using Windows.Media.Playback;

namespace MediaRecorder.App.ViewModels;

public sealed partial class RecordViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly CaptureDeviceInfo NoAudio = new("", "Kein Audio", CaptureDeviceKind.Audio);

    private readonly IRecordingBackend _backend;
    private readonly CaptureDeviceService _devices = new();
    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _clock;
    private readonly Stopwatch _elapsed = new();
    private readonly RecordingOptions _options;
    private bool _suppressReinit;
    private string? _currentFile;

    public RecordViewModel(DispatcherQueue ui, IRecordingBackend? backend = null)
    {
        _ui = ui;
        _backend = backend ?? new MediaCaptureRecordingBackend();
        _options = RecordingSettingsStore.Load();

        _outputFolder = _options.OutputFolder;
        _fileNamePattern = _options.FileNamePattern;
        LoadEncoding(_options.Encoding);
        UpdateCodecLists();

        _backend.StateChanged += (_, s) => _ui.TryEnqueue(() => State = s);
        _backend.Faulted += (_, f) => _ui.TryEnqueue(() => OnFaulted(f));
        _devices.DevicesChanged += (_, _) => _ui.TryEnqueue(async () => await RefreshDevicesAsync());
        _devices.DeviceRemoved += (_, id) => _ui.TryEnqueue(() =>
        {
            if (id == SelectedVideoDevice?.Id || id == SelectedAudioDevice?.Id)
                ShowError("Ein gewähltes Gerät wurde getrennt.");
        });

        _clock = _ui.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(250);
        _clock.Tick += (_, _) => UpdateClock();
    }

    // ------------------------------------------------------------ Geräte & Formate

    public ObservableCollection<CaptureDeviceInfo> VideoDevices { get; } = new();
    public ObservableCollection<CaptureDeviceInfo> AudioDevices { get; } = new();
    public ObservableCollection<VideoCaptureFormat> VideoFormats { get; } = new();

    [ObservableProperty] private CaptureDeviceInfo? _selectedVideoDevice;
    [ObservableProperty] private CaptureDeviceInfo? _selectedAudioDevice;
    [ObservableProperty] private VideoCaptureFormat? _selectedVideoFormat;
    [ObservableProperty] private MediaPlayer? _previewPlayer;

    partial void OnSelectedVideoDeviceChanged(CaptureDeviceInfo? value) { if (!_suppressReinit) _ = OpenDevicesAsync(); }
    partial void OnSelectedAudioDeviceChanged(CaptureDeviceInfo? value) { if (!_suppressReinit) _ = OpenDevicesAsync(); }
    partial void OnSelectedVideoFormatChanged(VideoCaptureFormat? value) { if (!_suppressReinit && value is not null) _ = ApplyFormatAsync(value, suggestBitrate: true); }

    // ------------------------------------------------------------ Encoding

    public ContainerFormat[] Containers { get; } = Enum.GetValues<ContainerFormat>();
    /// <summary>Nur Codecs, die im gewählten Container möglich sind.</summary>
    public ObservableCollection<VideoCodec> VideoCodecs { get; } = new();
    public ObservableCollection<AudioCodec> AudioCodecs { get; } = new();
    /// <summary>Der Windows-AAC-Encoder kann nur 96/128/160/192 kbit/s, WMA mehr.</summary>
    public ObservableCollection<double> AudioBitrates { get; } = new();

    [ObservableProperty] private ContainerFormat _container;
    [ObservableProperty] private VideoCodec _videoCodec;
    [ObservableProperty] private AudioCodec _audioCodec;
    /// <summary>Video-Bitrate in Mbit/s.</summary>
    [ObservableProperty] private double _videoBitrateMbps = 16;
    /// <summary>Audio-Bitrate in kbit/s (bei verlustfreien Codecs ignoriert).</summary>
    [ObservableProperty] private double _audioBitrateKbps = 192;
    [ObservableProperty] private string? _codecWarning;

    // ComboBox.SelectedItem ist object und wird beim Leeren der Liste null.
    // Diese Brücken-Properties ignorieren null statt einen ungültigen Cast zu riskieren.
    public object? ContainerItem { get => Container; set { if (value is ContainerFormat v) Container = v; } }
    public object? VideoCodecItem { get => VideoCodec; set { if (value is VideoCodec v) VideoCodec = v; } }
    public object? AudioCodecItem { get => AudioCodec; set { if (value is AudioCodec v) AudioCodec = v; } }
    public object? AudioBitrateItem { get => AudioBitrateKbps; set { if (value is double v) AudioBitrateKbps = v; } }

    partial void OnAudioBitrateKbpsChanged(double value) { OnPropertyChanged(nameof(AudioBitrateItem)); UpdateFreeSpace(); }

    partial void OnVideoCodecChanged(VideoCodec value)
    {
        OnPropertyChanged(nameof(VideoCodecItem));
        _ = ValidateCodecAsync();
    }
    partial void OnAudioCodecChanged(AudioCodec value)
    {
        OnPropertyChanged(nameof(AudioCodecItem));
        UpdateAudioBitrates();
        OnPropertyChanged(nameof(IsAudioBitrateEditable));
        _ = ValidateCodecAsync();
    }
    partial void OnContainerChanged(ContainerFormat value)
    {
        OnPropertyChanged(nameof(ContainerItem));
        UpdateCodecLists();
        _ = ValidateCodecAsync();
    }
    partial void OnVideoBitrateMbpsChanged(double value)
    {
        // NumberBox liefert NaN, wenn das Feld geleert wird.
        if (double.IsNaN(value) || value <= 0) { VideoBitrateMbps = 16; return; }
        UpdateFreeSpace();
    }

    public bool IsAudioBitrateEditable => !EncodingSettings.IsLossless(AudioCodec);

    private void UpdateCodecLists()
    {
        var v = EncodingSettings.VideoCodecsFor(Container);
        // Aufnahme bietet in MP4 zusätzlich FLAC/PCM an; ob der MP4-Sink sie schreibt, wird beim Start geprüft.
        var a = Container == ContainerFormat.Mp4
            ? new[] { AudioCodec.Aac, AudioCodec.Flac, AudioCodec.Pcm }
            : EncodingSettings.AudioCodecsFor(Container).ToArray();

        var keepV = VideoCodec; var keepA = AudioCodec;
        VideoCodecs.Clear(); foreach (var c in v) VideoCodecs.Add(c);
        AudioCodecs.Clear(); foreach (var c in a) AudioCodecs.Add(c);
        VideoCodec = v.Contains(keepV) ? keepV : v[0];
        AudioCodec = a.Contains(keepA) ? keepA : a[0];
        // ComboBox verliert beim Clear die Auswahl: neu melden.
        OnPropertyChanged(nameof(VideoCodecItem));
        OnPropertyChanged(nameof(AudioCodecItem));
        UpdateAudioBitrates();
    }

    private void UpdateAudioBitrates()
    {
        double[] rates = AudioCodec == AudioCodec.Aac ? [96, 128, 160, 192] : [128, 160, 192, 256, 320];
        var keep = AudioBitrateKbps;
        AudioBitrates.Clear(); foreach (var r in rates) AudioBitrates.Add(r);
        AudioBitrateKbps = rates.Contains(keep) ? keep : rates.Where(r => r <= keep).DefaultIfEmpty(rates[0]).Max();
        OnPropertyChanged(nameof(AudioBitrateItem));
    }

    // ------------------------------------------------------------ Ausgabe

    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private string _fileNamePattern;
    [ObservableProperty] private string _freeSpaceText = "";

    partial void OnOutputFolderChanged(string value) => UpdateFreeSpace();

    // ------------------------------------------------------------ Status

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording), nameof(IsPaused), nameof(IsBusy), nameof(CanEditSettings), nameof(RecordButtonText), nameof(StateText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordCommand), nameof(PauseCommand))]
    private RecordingState _state = RecordingState.Idle;

    public bool IsRecording => State is RecordingState.Recording or RecordingState.Paused;
    public bool IsPaused => State == RecordingState.Paused;
    public bool IsBusy => State is RecordingState.Initializing or RecordingState.Stopping;
    public bool CanEditSettings => !IsRecording && !IsBusy;
    public string RecordButtonText => IsRecording ? "Stopp" : "Aufnahme";

    public string StateText => State switch
    {
        RecordingState.Idle => "Bereit",
        RecordingState.Initializing => "Gerät wird geöffnet …",
        RecordingState.Previewing => "Vorschau",
        RecordingState.Recording => "Aufnahme läuft",
        RecordingState.Paused => "Pausiert",
        RecordingState.Stopping => "Datei wird abgeschlossen …",
        RecordingState.Faulted => "Fehler",
        _ => ""
    };

    [ObservableProperty] private string _elapsedText = "00:00:00";
    [ObservableProperty] private string _fileSizeText = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _infoMessage;

    partial void OnStateChanged(RecordingState value)
    {
        if (value == RecordingState.Recording) _elapsed.Start();
        else if (value == RecordingState.Paused) _elapsed.Stop();
        PreviewPlayer = _backend.PreviewPlayer;
    }

    // ------------------------------------------------------------ Lebenszyklus

    public async Task InitializeAsync()
    {
        _devices.StartWatching();
        await RefreshDevicesAsync();
        UpdateFreeSpace();
        await ValidateCodecAsync();
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        try
        {
            var video = await _devices.GetVideoDevicesAsync();
            var audio = await _devices.GetAudioDevicesAsync();

            _suppressReinit = true;
            var prevVideo = SelectedVideoDevice?.Id ?? _options.VideoDeviceId;
            var prevAudio = SelectedAudioDevice?.Id ?? _options.AudioDeviceId;

            VideoDevices.Clear();
            foreach (var d in video) VideoDevices.Add(d);
            AudioDevices.Clear();
            AudioDevices.Add(NoAudio);
            foreach (var d in audio) AudioDevices.Add(d);

            var newVideo = VideoDevices.FirstOrDefault(d => d.Id == prevVideo) ?? VideoDevices.FirstOrDefault();
            var newAudio = prevAudio is null
                ? AudioDevices.FirstOrDefault(d => d.IsDefault) ?? NoAudio
                : AudioDevices.FirstOrDefault(d => d.Id == prevAudio) ?? AudioDevices.FirstOrDefault(d => d.IsDefault) ?? NoAudio;

            bool changed = newVideo?.Id != SelectedVideoDevice?.Id || newAudio.Id != SelectedAudioDevice?.Id;
            SelectedVideoDevice = newVideo;
            SelectedAudioDevice = newAudio;
            _suppressReinit = false;
            _ = ValidateCodecAsync();

            if (VideoDevices.Count == 0)
            {
                ShowError("Kein Videoeingang gefunden. Bitte Kamera oder Grabber anschließen.");
                return;
            }
            // Während einer Aufnahme nie automatisch umschalten.
            if ((changed || State is RecordingState.Idle or RecordingState.Faulted) && !IsRecording)
                await OpenDevicesAsync();
        }
        catch (Exception ex)
        {
            _suppressReinit = false;
            ShowError("Geräte konnten nicht gelesen werden: " + ex.Message);
        }
    }

    private async Task OpenDevicesAsync()
    {
        if (SelectedVideoDevice is null || IsRecording) return;
        ErrorMessage = null;
        try
        {
            var audioId = string.IsNullOrEmpty(SelectedAudioDevice?.Id) ? null : SelectedAudioDevice!.Id;
            await _backend.InitializeAsync(SelectedVideoDevice.Id, audioId);

            _suppressReinit = true;
            VideoFormats.Clear();
            foreach (var f in _backend.SupportedFormats) VideoFormats.Add(f);
            var wanted = VideoFormats.FirstOrDefault(f => VideoFormatKey.For(f) == _options.VideoFormatKey)
                      ?? BestDefaultFormat();
            SelectedVideoFormat = wanted;
            _suppressReinit = false;

            // Gespeichertes Format: gespeicherte Bitrate behalten; sonst passende Bitrate vorschlagen.
            bool restored = wanted is not null && VideoFormatKey.For(wanted) == _options.VideoFormatKey;
            if (wanted is not null) await ApplyFormatAsync(wanted, suggestBitrate: !restored);
            PreviewPlayer = _backend.PreviewPlayer;
            SaveOptions();
        }
        catch (Exception ex)
        {
            _suppressReinit = false;
            PreviewPlayer = null;
            ShowError(ex.Message);
        }
    }

    /// <summary>Höchste Auflösung bis 4K mit mindestens 25 fps, unkomprimiert bevorzugt.</summary>
    private VideoCaptureFormat? BestDefaultFormat() =>
        VideoFormats.Where(f => f.FrameRate >= 24.9 && f.Width <= 3840)
                    .OrderByDescending(f => f.Width * f.Height)
                    .ThenByDescending(f => f.FrameRate)
                    .ThenBy(f => f.IsCompressed)
                    .FirstOrDefault()
        ?? VideoFormats.FirstOrDefault();

    private async Task ApplyFormatAsync(VideoCaptureFormat format, bool suggestBitrate)
    {
        try
        {
            await _backend.SetVideoFormatAsync(format);
            PreviewPlayer = _backend.PreviewPlayer;
            if (suggestBitrate) SuggestBitrate(format);
            SaveOptions();
        }
        catch (Exception ex)
        {
            ShowError("Format konnte nicht gesetzt werden: " + ex.Message);
        }
    }

    /// <summary>Schlägt eine Bitrate passend zu Auflösung, FPS und Codec vor (Bits pro Pixel).</summary>
    private void SuggestBitrate(VideoCaptureFormat f)
    {
        // Grob: Bits pro Pixel für sehr gute Qualität bei Live-Material.
        double bpp = VideoCodec switch { VideoCodec.H264 => 0.10, VideoCodec.Hevc => 0.06, _ => 0.12 };
        var mbps = f.Width * f.Height * f.FrameRate * bpp / 1_000_000;
        VideoBitrateMbps = Math.Clamp(Math.Round(mbps), 2, 200);
    }

    private async Task ValidateCodecAsync()
    {
        try
        {
            var settings = BuildEncodingSettings();
            CodecWarning = settings.Validate().FirstOrDefault() ?? await _backend.CheckSupportAsync(settings);
        }
        catch (Exception ex)
        {
            CodecWarning = "Codec-Prüfung fehlgeschlagen: " + ex.Message;
        }
    }

    // ------------------------------------------------------------ Befehle

    private bool CanToggleRecord() => !IsBusy && (IsRecording || State == RecordingState.Previewing);

    [RelayCommand(CanExecute = nameof(CanToggleRecord))]
    private async Task ToggleRecordAsync()
    {
        if (IsRecording) await StopAsync();
        else await StartAsync();
    }

    private async Task StartAsync()
    {
        ErrorMessage = null;
        InfoMessage = null;
        var settings = BuildEncodingSettings();
        var problem = settings.Validate().FirstOrDefault() ?? await _backend.CheckSupportAsync(settings);
        if (problem is not null) { ShowError(problem); return; }

        try
        {
            SaveOptions();
            _currentFile = _options.BuildFilePath(settings.FileExtension, SelectedVideoDevice?.Name);
            if (_backend is MediaCaptureRecordingBackend mc) mc.MinimumFreeSpaceMb = _options.MinimumFreeSpaceMb;

            await _backend.StartRecordingAsync(_currentFile, settings);
            _elapsed.Reset();
            _elapsed.Start();
            _clock.Start();
        }
        catch (Exception ex)
        {
            ShowError("Aufnahme konnte nicht gestartet werden: " + ex.Message);
        }
    }

    private async Task StopAsync()
    {
        var path = await _backend.StopRecordingAsync();
        _clock.Stop();
        _elapsed.Stop();
        UpdateClock();
        if (path is not null) InfoMessage = $"Gespeichert: {path}";
        UpdateFreeSpace();
    }

    private bool CanPause() => IsRecording;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseAsync()
    {
        try
        {
            if (IsPaused) await _backend.ResumeAsync();
            else await _backend.PauseAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(OutputFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutputFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>Von der Page aufgerufen, nachdem der FolderPicker einen Ordner geliefert hat.</summary>
    public void SetOutputFolder(string path)
    {
        OutputFolder = path;
        SaveOptions();
    }

    // ------------------------------------------------------------ Hilfen

    private void OnFaulted(RecordingFault f)
    {
        _clock.Stop();
        _elapsed.Stop();
        ShowError(f.Message + (f.SavedFilePath is not null ? $"\nGesichert: {f.SavedFilePath}" : ""));
        PreviewPlayer = _backend.PreviewPlayer;
        UpdateFreeSpace();
    }

    private void ShowError(string message) => ErrorMessage = message;

    private void UpdateClock()
    {
        var t = _elapsed.Elapsed;
        ElapsedText = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        try
        {
            if (_currentFile is not null && File.Exists(_currentFile))
                FileSizeText = FormatBytes(new FileInfo(_currentFile).Length);
        }
        catch { }
        if (t.Seconds % 10 == 0) UpdateFreeSpace();
    }

    private void UpdateFreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(OutputFolder));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) { FreeSpaceText = ""; return; }
            var drive = new DriveInfo(root);
            var bytesPerSec = (VideoBitrateMbps * 1_000_000 + AudioBitrateKbps * 1000) / 8;
            var minutes = bytesPerSec > 0 ? drive.AvailableFreeSpace / bytesPerSec / 60 : 0;
            FreeSpaceText = $"{FormatBytes(drive.AvailableFreeSpace)} frei, reicht für ca. {FormatDuration(minutes)}";
        }
        catch { FreeSpaceText = ""; }
    }

    private static string FormatDuration(double minutes) =>
        minutes >= 120 ? $"{minutes / 60:0} Std." : $"{minutes:0} Min.";

    private static string FormatBytes(long b) => b switch
    {
        >= 1L << 40 => $"{b / (double)(1L << 40):0.0} TB",
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0} MB",
        _ => $"{b / 1024.0:0} KB"
    };

    // Einzige Stelle, die die Seite auf die gemeinsamen EncodingSettings abbildet.
    private EncodingSettings BuildEncodingSettings() => new()
    {
        Container = Container,
        VideoCodec = VideoCodec,
        AudioCodec = AudioCodec,
        VideoBitrateKbps = (int)Math.Round(VideoBitrateMbps * 1000),
        AudioBitrateKbps = (int)Math.Round(AudioBitrateKbps),
        IncludeVideo = true,
        IncludeAudio = !string.IsNullOrEmpty(SelectedAudioDevice?.Id),
        // Auflösung und FPS: 0 = 1:1 von der Quelle übernehmen.
        Width = 0,
        Height = 0,
        FrameRate = 0,
    };

    private void LoadEncoding(EncodingSettings e)
    {
        _container = e.Container;
        _videoCodec = e.VideoCodec;
        _audioCodec = e.AudioCodec;
        if (e.VideoBitrateKbps > 0) _videoBitrateMbps = e.VideoBitrateKbps / 1000.0;
        if (e.AudioBitrateKbps > 0) _audioBitrateKbps = e.AudioBitrateKbps;
    }

    private void SaveOptions()
    {
        _options.VideoDeviceId = SelectedVideoDevice?.Id;
        _options.AudioDeviceId = SelectedAudioDevice?.Id;
        _options.VideoFormatKey = SelectedVideoFormat is null ? null : VideoFormatKey.For(SelectedVideoFormat);
        _options.Encoding = BuildEncodingSettings();
        _options.OutputFolder = OutputFolder;
        _options.FileNamePattern = FileNamePattern;
        RecordingSettingsStore.Save(_options);
    }

    public async ValueTask DisposeAsync()
    {
        _clock.Stop();
        if (IsRecording) await StopAsync();
        SaveOptions();
        _devices.Dispose();
        await _backend.DisposeAsync();
    }
}
