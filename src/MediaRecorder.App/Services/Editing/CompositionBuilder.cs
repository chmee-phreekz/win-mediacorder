using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MediaRecorder.Core.Editing;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Storage;

namespace MediaRecorder.App.Services.Editing;

/// <summary>
/// Übersetzt das Timeline-Modell in eine Windows.Media.Editing.MediaComposition (Media Foundation),
/// dieselbe Medienbasis wie die Aufnahmeseite (MediaCapture).
///
/// Abbildung:
/// - Grundbild: ein schwarzes Standbild in exakt der Ausgabegröße über die ganze Länge.
///   Damit ist das Koordinatensystem der Komposition immer genau die Ausgabegröße.
/// - V1 und V2 -> je eine MediaOverlayLayer (V2 darüber). Jedes Overlay bekommt per AspectFit das größte
///   unverzerrte Rechteck in diesem Koordinatensystem.
/// - Reine Audiodateien (beide Spuren) -> BackgroundAudioTracks mit passender Verzögerung.
///
/// Warum nicht V1 direkt als MediaComposition.Clips: Die Komposition übernimmt dann Größe und
/// Seitenverhältnis des ersten Clips als Bezugsraum für Overlay-Positionen. Bei Clips mit anderem Format
/// als das Projekt (oder schwarzen Lückenclips ohne feste Größe) wurde V2 dadurch verzerrt dargestellt.
/// </summary>
public sealed class CompositionBuilder
{
    private readonly Dictionary<Guid, StorageFile> _files = new();
    private readonly Dictionary<Guid, MediaClip> _videoClips = new();
    private readonly Dictionary<Guid, BackgroundAudioTrack> _audioTracks = new();
    private readonly Dictionary<(int, int), StorageFile> _baseImages = new();

    /// <param name="width">Ausgabebreite; alle Positionen werden in diesem Pixelraum berechnet.</param>
    /// <param name="height">Ausgabehöhe.</param>
    public async Task<MediaComposition> BuildAsync(Timeline timeline, int width, int height)
    {
        var composition = new MediaComposition();
        var total = timeline.Duration;
        if (total <= TimeSpan.Zero) return composition;

        composition.Clips.Add(await MediaClip.CreateFromImageFileAsync(await GetBaseImageAsync(width, height), total));

        foreach (int track in new[] { Timeline.MainTrack, Timeline.OverlayTrack })
        {
            var layer = new MediaOverlayLayer();
            foreach (var c in timeline.GetClips(track))
            {
                var asset = timeline.GetAsset(c.AssetId);
                if (asset.Kind == MediaKind.Audio)
                {
                    composition.BackgroundAudioTracks.Add(await CreateAudioAsync(asset, c));
                    continue;
                }

                var r = Geometry.AspectFitPixels(asset.Width, asset.Height, width, height);
                layer.Overlays.Add(new MediaOverlay(await CreateVideoClipAsync(asset, c))
                {
                    Position = new Rect(r.X, r.Y, r.Width, r.Height),
                    Delay = c.Start,
                    Opacity = 1.0,
                    AudioEnabled = asset.HasAudio && !c.AudioMuted,
                });
            }
            if (layer.Overlays.Count > 0)
                composition.OverlayLayers.Add(layer);
        }

        return composition;
    }

    /// <summary>Schwarzes PNG in Ausgabegröße, einmal pro Größe im Temp-Ordner erzeugt.</summary>
    private async Task<StorageFile> GetBaseImageAsync(int width, int height)
    {
        if (_baseImages.TryGetValue((width, height), out var cached)) return cached;
        var dir = Path.Combine(Path.GetTempPath(), "MediaRecorder");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"base_{width}x{height}.png");
        if (!File.Exists(path))
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, SolidPng.Create(width, height));
            File.Move(tmp, path, overwrite: true);
        }
        var file = await StorageFile.GetFileFromPathAsync(path);
        _baseImages[(width, height)] = file;
        return file;
    }

    /// <summary>Gibt zwischengespeicherte Dateien/Clips frei, z. B. nachdem ein Medium aus dem Pool entfernt wurde.</summary>
    public void Forget(Guid assetId)
    {
        _files.Remove(assetId);
        _videoClips.Remove(assetId);
        _audioTracks.Remove(assetId);
    }

    private async Task<MediaClip> CreateVideoClipAsync(MediaAsset asset, TimelineClip c)
    {
        var file = await GetFileAsync(asset);
        if (asset.Kind == MediaKind.Image)
            return await MediaClip.CreateFromImageFileAsync(file, c.Duration);

        if (!_videoClips.TryGetValue(asset.Id, out var template))
        {
            template = await MediaClip.CreateFromFileAsync(file);
            _videoClips[asset.Id] = template;
        }

        // Jeder Einsatz braucht eine eigene Instanz; Clone() spart das erneute Öffnen der Datei.
        var clip = template.Clone();
        clip.TrimTimeFromStart = c.SourceIn;
        clip.TrimTimeFromEnd = Positive(template.OriginalDuration - c.SourceOut);
        clip.Volume = c.AudioMuted ? 0.0 : 1.0;
        return clip;
    }

    private async Task<BackgroundAudioTrack> CreateAudioAsync(MediaAsset asset, TimelineClip c)
    {
        if (!_audioTracks.TryGetValue(asset.Id, out var template))
        {
            template = await BackgroundAudioTrack.CreateFromFileAsync(await GetFileAsync(asset));
            _audioTracks[asset.Id] = template;
        }
        var track = template.Clone();
        track.TrimTimeFromStart = c.SourceIn;
        track.TrimTimeFromEnd = Positive(template.OriginalDuration - c.SourceOut);
        track.Delay = c.Start;
        track.Volume = c.AudioMuted ? 0.0 : 1.0;
        return track;
    }

    private async Task<StorageFile> GetFileAsync(MediaAsset asset)
    {
        if (!_files.TryGetValue(asset.Id, out var file))
        {
            try
            {
                file = await StorageFile.GetFileFromPathAsync(asset.Path);
            }
            catch (Exception ex) when (ex is System.IO.FileNotFoundException or UnauthorizedAccessException)
            {
                throw new MediaImportException($"Datei nicht mehr verfügbar: {asset.Path}", ex);
            }
            _files[asset.Id] = file;
        }
        return file;
    }

    private static TimeSpan Positive(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}
