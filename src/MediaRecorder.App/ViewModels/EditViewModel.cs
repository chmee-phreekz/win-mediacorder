using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaRecorder.App.Services.Editing;
using MediaRecorder.Core.Editing;
using MediaRecorder.Core.Encoding;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace MediaRecorder.App.ViewModels;

/// <summary>Ausgabeformat des Projekts: bestimmt Seitenverhältnis von Vorschau und Export.</summary>
public sealed record ProjectFormat(string Name, int Width, int Height, double FrameRate)
{
    public double AspectRatio => Width / (double)Height;
    public override string ToString() => Name;

    public static IReadOnlyList<ProjectFormat> All { get; } =
    [
        new("1080p 16:9 · 30 fps", 1920, 1080, 30),
        new("1080p 16:9 · 25 fps", 1920, 1080, 25),
        new("1080p 16:9 · 60 fps", 1920, 1080, 60),
        new("720p 16:9 · 30 fps", 1280, 720, 30),
        new("2160p 16:9 · 30 fps", 3840, 2160, 30),
        new("Hochkant 9:16 · 30 fps", 1080, 1920, 30),
        new("Quadrat 1:1 · 30 fps", 1080, 1080, 30),
        new("4:3 · 30 fps", 1440, 1080, 30),
    ];

    /// <summary>Passendes Format zur ersten Videoquelle (Seitenverhältnis und Bildrate).</summary>
    public static ProjectFormat SuggestFor(MediaAsset asset)
    {
        double ar = asset.AspectRatio;
        var candidates = All.OrderBy(f => Math.Abs(f.AspectRatio - ar))
            .ThenBy(f => Math.Abs(f.FrameRate - (asset.FrameRate > 0 ? asset.FrameRate : 30)))
            .ThenBy(f => Math.Abs(f.Height - Math.Min(asset.Height, asset.Width)));
        return candidates.First();
    }
}

/// <summary>Eintrag im Medienpool.</summary>
public sealed partial class MediaAssetItem : ObservableObject
{
    public MediaAssetItem(MediaAsset asset) => Asset = asset;

    public MediaAsset Asset { get; }
    public string Name => Asset.DisplayName;
    public string Glyph => Asset.Kind switch { MediaKind.Audio => "", MediaKind.Image => "", _ => "" };

    public string Info => Asset.Kind switch
    {
        MediaKind.Audio => $"Audio · {Timecode.Format(Asset.Duration, 0)[..8]}",
        MediaKind.Image => $"Bild · {Asset.Width}×{Asset.Height}",
        _ => $"{Asset.Width}×{Asset.Height} · {Asset.FrameRate:0.##} fps · {Timecode.Format(Asset.Duration, 0)[..8]}{(Asset.HasAudio ? "" : " · ohne Ton")}",
    };

    [ObservableProperty] private BitmapImage? _thumbnail;
}

public sealed partial class EditViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly MediaImportService _import = new();
    private readonly PreviewController _preview = new();
    private readonly ExportService _export = new();
    private readonly WaveformService _waveforms = new();
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _positionTimer;
    private CancellationTokenSource? _exportCts;
    private bool _formatChosenByUser;

    public EditViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Timeline.Changed += (_, _) => OnTimelineChanged();

        // Vorschau nicht bei jedem Mausschritt neu bauen, sondern kurz nach der letzten Änderung.
        _refreshTimer = dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(250);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += async (_, _) => await RefreshPreviewAsync();

        _positionTimer = dispatcher.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(33);
        _positionTimer.Tick += (_, _) => OnPositionTick();

        _preview.Player.PlaybackSession.PlaybackStateChanged += (s, _) =>
            _dispatcher.TryEnqueue(() => OnPlaybackStateChanged(s.PlaybackState));
        _preview.Player.MediaFailed += (_, e) =>
            _dispatcher.TryEnqueue(() => ErrorMessage = $"Vorschau-Fehler: {e.ErrorMessage}");
        _preview.PreviewFailed += (_, msg) =>
            _dispatcher.TryEnqueue(() => ErrorMessage = $"Vorschau-Fehler: {msg}");

        _waveforms.WaveformReady += (_, _) => _dispatcher.TryEnqueue(() => WaveformsChanged?.Invoke(this, EventArgs.Empty));

        _format = ProjectFormat.All[0];
    }

    public Timeline Timeline { get; } = new();
    public ObservableCollection<MediaAssetItem> Assets { get; } = new();
    public IReadOnlyList<ProjectFormat> Formats => ProjectFormat.All;
    public MediaPlayer Player => _preview.Player;

    [ObservableProperty] private MediaAssetItem? _selectedAsset;
    [ObservableProperty] private Guid? _selectedClipId;
    [ObservableProperty] private TimeSpan _playhead;
    [ObservableProperty] private TimeSpan? _inPoint;
    [ObservableProperty] private TimeSpan? _outPoint;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _infoMessage;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private double _exportProgress;
    [ObservableProperty] private ProjectFormat _format;

    public string PlayheadText => Timecode.Format(Playhead, Format.FrameRate);
    public string DurationText => Timecode.Format(Timeline.Duration, Format.FrameRate);
    public bool HasClips => !Timeline.IsEmpty;
    public bool HasSelection => SelectedClipId is not null;
    public bool HasRange => InPoint is not null && OutPoint is not null && OutPoint > InPoint;
    public bool ShowEmptyHint => Timeline.IsEmpty;
    public double PreviewAspect => Format.AspectRatio;

    public string RangeText => (InPoint, OutPoint) switch
    {
        ({ } i, { } o) when o > i => $"In {Timecode.Format(i, Format.FrameRate)} · Out {Timecode.Format(o, Format.FrameRate)} · {Timecode.Format(o - i, Format.FrameRate)}",
        ({ } i, null) => $"In {Timecode.Format(i, Format.FrameRate)}",
        (null, { } o) => $"Out {Timecode.Format(o, Format.FrameRate)}",
        _ => "Kein Bereich (I / O setzen)",
    };

    public bool IsSelectedClipMuted => SelectedClipId is { } id && Timeline.Find(id)?.AudioMuted == true;

    public string PlayGlyph => IsPlaying ? "\uE769" : "\uE768";
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasInfo => !string.IsNullOrEmpty(InfoMessage);
    public bool CanExport => !Timeline.IsEmpty && !IsExporting;

    partial void OnPlayheadChanged(TimeSpan value) => OnPropertyChanged(nameof(PlayheadText));
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayGlyph));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnInfoMessageChanged(string? value) => OnPropertyChanged(nameof(HasInfo));
    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(CanExport));
    partial void OnSelectedClipIdChanged(Guid? value) => NotifyEditState();
    partial void OnInPointChanged(TimeSpan? value) => NotifyRange();
    partial void OnOutPointChanged(TimeSpan? value) => NotifyRange();
    partial void OnSelectedAssetChanged(MediaAssetItem? value) => NotifyEditState();

    partial void OnFormatChanged(ProjectFormat value)
    {
        _formatChosenByUser = true;
        OnPropertyChanged(nameof(PreviewAspect));
        OnPropertyChanged(nameof(PlayheadText));
        OnPropertyChanged(nameof(DurationText));
        SchedulePreviewRefresh();
    }

    // ---------- Import ----------

    public async Task ImportFilesAsync(IReadOnlyList<StorageFile> files)
    {
        if (files.Count == 0) return;
        IsBusy = true;
        var failures = new List<string>();
        try
        {
            foreach (var file in files)
            {
                if (Assets.Any(a => string.Equals(a.Asset.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                    continue; // schon im Pool
                try
                {
                    var asset = await _import.ImportAsync(file);
                    Timeline.RegisterAsset(asset);
                    var item = new MediaAssetItem(asset);
                    Assets.Add(item);
                    SelectedAsset ??= item;
                    _ = LoadThumbnailAsync(item, file);
                    _ = _waveforms.LoadAsync(asset);
                }
                catch (MediaImportException ex)
                {
                    failures.Add(ex.Message);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
        ErrorMessage = failures.Count > 0 ? string.Join(Environment.NewLine, failures) : null;
    }

    private static async Task LoadThumbnailAsync(MediaAssetItem item, StorageFile file)
    {
        try
        {
            var mode = item.Asset.Kind switch
            {
                MediaKind.Audio => ThumbnailMode.MusicView,
                MediaKind.Image => ThumbnailMode.PicturesView,
                _ => ThumbnailMode.VideosView,
            };
            using var thumb = await file.GetThumbnailAsync(mode, 160, ThumbnailOptions.UseCurrentScale);
            if (thumb is null) return;
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(thumb);
            item.Thumbnail = bmp;
        }
        catch
        {
            // Vorschaubild ist optional; Glyph bleibt sichtbar.
        }
    }

    /// <summary>Wie viele Clips auf der Timeline das Medium verwenden (für die Sicherheitsabfrage).</summary>
    public int CountClipsUsing(MediaAssetItem item) => Timeline.CountClipsUsing(item.Asset.Id);

    /// <summary>
    /// Entfernt ein Medium aus dem Pool, auch wenn es im Rückgängig-Verlauf vorkommt.
    /// Clips auf der Timeline, die es verwenden, werden mit entfernt.
    /// </summary>
    [RelayCommand]
    private void RemoveAsset(MediaAssetItem? item)
    {
        item ??= SelectedAsset;
        if (item is null) return;
        int removedClips = Timeline.RemoveAsset(item.Asset.Id);
        _preview.Builder.Forget(item.Asset.Id);
        _waveforms.Forget(item.Asset.Id);
        int index = Assets.IndexOf(item);
        Assets.Remove(item);
        if (SelectedAsset == item || SelectedAsset is null)
            SelectedAsset = Assets.Count == 0 ? null : Assets[Math.Clamp(index, 0, Assets.Count - 1)];
        InfoMessage = removedClips > 0
            ? $"„{item.Name}“ entfernt, dazu {removedClips} Clip(s) von der Timeline."
            : null;
    }

    /// <summary>Wellenform eines Mediums, sobald berechnet.</summary>
    public Waveform? GetWaveform(Guid assetId) => _waveforms.Get(assetId);

    /// <summary>Eine Wellenform ist fertig (auf dem UI-Thread).</summary>
    public event EventHandler? WaveformsChanged;

    // ---------- Timeline-Bearbeitung ----------

    [RelayCommand] private void AddToMainTrack() => AddSelectedAsset(Timeline.MainTrack, null);
    [RelayCommand] private void AddToOverlayTrack() => AddSelectedAsset(Timeline.OverlayTrack, Playhead);

    /// <summary>V1: ans Spurende anhängen. V2: am Abspielkopf einfügen (bzw. nächste freie Stelle).</summary>
    public void AddAsset(Guid assetId, int track, TimeSpan? at)
    {
        try
        {
            var asset = Timeline.GetAsset(assetId);
            if (Timeline.IsEmpty && asset.Kind == MediaKind.Video && !_formatChosenByUser)
            {
                Format = ProjectFormat.SuggestFor(asset);
                _formatChosenByUser = false; // automatisch gewählt, darf beim nächsten leeren Projekt wieder angepasst werden
                InfoMessage = $"Projektformat an „{asset.DisplayName}“ angepasst: {Format.Name}. Änderbar oben rechts.";
            }
            var clip = Timeline.AddClip(assetId, track, at);
            SelectedClipId = clip.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void AddSelectedAsset(int track, TimeSpan? at)
    {
        if (SelectedAsset is { } item) AddAsset(item.Asset.Id, track, at);
    }

    [RelayCommand]
    private void Split()
    {
        var ids = Timeline.Split(Playhead, SelectedClipId is { } id && Timeline.Find(id)?.Contains(Playhead) == true ? id : null);
        if (ids.Count == 0)
            InfoMessage = "Am Abspielkopf liegt kein Clip, der geteilt werden kann.";
        else
            SelectedClipId = ids[0];
    }

    [RelayCommand]
    private void DeleteClip()
    {
        if (SelectedClipId is { } id) Timeline.RemoveClip(id, ripple: false);
    }

    [RelayCommand]
    private void RippleDeleteClip()
    {
        if (SelectedClipId is { } id) Timeline.RemoveClip(id, ripple: true);
    }

    [RelayCommand] private void SetInPoint() => InPoint = Playhead;
    [RelayCommand] private void SetOutPoint() => OutPoint = Playhead;

    [RelayCommand]
    private void ClearRange()
    {
        InPoint = null;
        OutPoint = null;
    }

    /// <summary>Schneidet den In/Out-Bereich auf beiden Spuren heraus und schließt die Lücke.</summary>
    [RelayCommand]
    private void CutRange()
    {
        if (!HasRange)
        {
            InfoMessage = "Zuerst mit I und O einen Bereich markieren.";
            return;
        }
        var start = InPoint!.Value;
        if (Timeline.CutRange(start, OutPoint!.Value))
        {
            ClearRange();
            Seek(start);
        }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (SelectedClipId is { } id && Timeline.Find(id) is { } c)
            Timeline.SetAudioMuted(id, !c.AudioMuted);
    }

    public bool CanUndo => Timeline.CanUndo;
    public bool CanRedo => Timeline.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))] private void Undo() => Timeline.Undo();
    [RelayCommand(CanExecute = nameof(CanRedo))] private void Redo() => Timeline.Redo();

    // ---------- Wiedergabe ----------

    [RelayCommand]
    private void PlayPause()
    {
        if (Timeline.IsEmpty) return;
        if (_preview.IsPlaying) _preview.Pause();
        else
        {
            if (Playhead >= Timeline.Duration) Seek(TimeSpan.Zero);
            _preview.Play();
        }
    }

    [RelayCommand] private void StepForward() => Seek(Playhead + Timecode.FrameDuration(Format.FrameRate));
    [RelayCommand] private void StepBack() => Seek(Playhead - Timecode.FrameDuration(Format.FrameRate));
    [RelayCommand] private void GoToStart() => Seek(TimeSpan.Zero);
    [RelayCommand] private void GoToEnd() => Seek(Timeline.Duration);

    public void Seek(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t > Timeline.Duration) t = Timeline.Duration;
        Playhead = t;
        _preview.Seek(t);
    }

    private void OnPlaybackStateChanged(MediaPlaybackState state)
    {
        IsPlaying = state == MediaPlaybackState.Playing;
        if (IsPlaying) _positionTimer.Start();
        else _positionTimer.Stop();
    }

    private void OnPositionTick()
    {
        var pos = _preview.Position;
        Playhead = pos;
        if (pos >= Timeline.Duration)
            _preview.Pause();
    }

    // ---------- Export ----------

    public EncodingSettings DefaultExportSettings() => new()
    {
        Width = Format.Width,
        Height = Format.Height,
        FrameRate = Format.FrameRate,
        VideoBitrateKbps = Format.Width * Format.Height >= 3840 * 2160 ? 40_000 : Format.Width * Format.Height <= 1280 * 720 ? 5_000 : 12_000,
    };

    public async Task ExportAsync(EncodingSettings settings, string targetPath)
    {
        if (IsExporting) return;
        _preview.Pause();
        _exportCts = new CancellationTokenSource();
        IsExporting = true;
        ExportProgress = 0;
        ErrorMessage = null;
        try
        {
            var progress = new Progress<double>(p => ExportProgress = p); // Progress<T> meldet auf dem UI-Thread
            await _export.ExportAsync(Timeline, settings, targetPath, progress, _exportCts.Token);
            InfoMessage = $"Export abgeschlossen: {targetPath}";
        }
        catch (OperationCanceledException)
        {
            InfoMessage = "Export abgebrochen.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Export fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
            _exportCts.Dispose();
            _exportCts = null;
        }
    }

    [RelayCommand]
    private void CancelExport() => _exportCts?.Cancel();

    // ---------- intern ----------

    private void OnTimelineChanged()
    {
        if (Timeline.IsEmpty) _formatChosenByUser = false;
        if (Playhead > Timeline.Duration) Playhead = Timeline.Duration;
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(HasClips));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(CanExport));
        NotifyEditState();
        SchedulePreviewRefresh();
    }

    private void SchedulePreviewRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private async Task RefreshPreviewAsync()
    {
        try
        {
            // Vorschau in reduzierter Auflösung (max. 1280 px Kantenlänge), gleiches Seitenverhältnis wie der Export.
            double scale = Math.Min(1.0, 1280.0 / Math.Max(Format.Width, Format.Height));
            int w = Even(Format.Width * scale), h = Even(Format.Height * scale);
            await _preview.RefreshAsync(Timeline, w, h, Playhead);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Vorschau konnte nicht erstellt werden: {ex.Message}";
        }
    }

    private void NotifyEditState()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSelectedClipMuted));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void NotifyRange()
    {
        OnPropertyChanged(nameof(HasRange));
        OnPropertyChanged(nameof(RangeText));
    }

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    public async ValueTask DisposeAsync()
    {
        _exportCts?.Cancel();
        _refreshTimer.Stop();
        _positionTimer.Stop();
        // Kurz warten, damit ein abgebrochener Export seine temporäre Datei aufräumen kann.
        for (int i = 0; i < 20 && IsExporting; i++)
            await Task.Delay(100);
        _waveforms.Dispose();
        _preview.Dispose();
    }
}
