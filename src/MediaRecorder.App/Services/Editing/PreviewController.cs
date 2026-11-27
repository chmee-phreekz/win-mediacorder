using System;
using System.Threading.Tasks;
using MediaRecorder.Core.Editing;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace MediaRecorder.App.Services.Editing;

/// <summary>
/// Hält den MediaPlayer der Vorschau und erzeugt nach Timeline-Änderungen einen neuen Vorschau-Stream
/// (MediaComposition.GeneratePreviewMediaStreamSource). Position und Wiedergabezustand bleiben dabei erhalten.
/// </summary>
public sealed class PreviewController : IDisposable
{
    private readonly CompositionBuilder _builder = new();
    private int _generation;
    private TimeSpan? _pendingSeek;
    private bool _resumeAfterOpen;

    public PreviewController()
    {
        Player = new MediaPlayer { AutoPlay = false, IsLoopingEnabled = false };
        Player.MediaOpened += OnMediaOpened;
    }

    public MediaPlayer Player { get; }

    /// <summary>Fehler beim Öffnen des Vorschau-Streams (Aufruf auf einem Hintergrund-Thread).</summary>
    public event EventHandler<string>? PreviewFailed;

    public bool IsPlaying => Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public TimeSpan Position => Player.PlaybackSession.Position;

    public CompositionBuilder Builder => _builder;

    /// <summary>Baut die Vorschau neu. Ältere, noch laufende Aufrufe werden verworfen.</summary>
    public async Task RefreshAsync(Timeline timeline, int width, int height, TimeSpan position)
    {
        int generation = ++_generation;
        bool wasPlaying = IsPlaying;

        if (timeline.IsEmpty)
        {
            SwapSource(null);
            return;
        }

        var composition = await _builder.BuildAsync(timeline, width, height);
        if (generation != _generation) return; // inzwischen gab es eine neuere Änderung

        var stream = composition.GeneratePreviewMediaStreamSource(width, height);
        _pendingSeek = position < timeline.Duration ? position : timeline.Duration;
        _resumeAfterOpen = wasPlaying;
        SwapSource(MediaSource.CreateFromMediaStreamSource(stream));
    }

    public void Play() => Player.Play();
    public void Pause() => Player.Pause();

    public void Seek(TimeSpan position)
    {
        if (Player.Source is null) return;
        Player.PlaybackSession.Position = position;
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        if (_pendingSeek is { } seek)
            sender.PlaybackSession.Position = seek;
        _pendingSeek = null;
        if (_resumeAfterOpen) sender.Play();
        _resumeAfterOpen = false;
    }

    private void SwapSource(MediaSource? source)
    {
        var old = Player.Source as MediaSource;
        try
        {
            Player.Source = source;
        }
        catch (Exception ex)
        {
            PreviewFailed?.Invoke(this, ex.Message);
        }
        old?.Dispose();
    }

    public void Dispose()
    {
        _generation++;
        Player.MediaOpened -= OnMediaOpened;
        SwapSource(null);
        Player.Dispose();
    }
}
