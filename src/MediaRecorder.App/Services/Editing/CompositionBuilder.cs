using System;
using System.Collections.Generic;
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
/// - V1 (Hauptspur) -> MediaComposition.Clips, Lücken als schwarze Farbclips.
/// - V2 (Overlay)   -> eine MediaOverlayLayer, jedes Overlay seitenrichtig und größtmöglich im Ausgabebild (AspectFit).
/// - Reine Audiodateien (beide Spuren) -> BackgroundAudioTracks mit passender Verzögerung.
/// Ragt V2 über das Ende von V1 hinaus, wird V1 mit Schwarz aufgefüllt, damit das Overlay sichtbar bleibt.
/// </summary>
public sealed class CompositionBuilder
{
    private readonly Dictionary<Guid, StorageFile> _files = new();
    private readonly Dictionary<Guid, MediaClip> _videoClips = new();
    private readonly Dictionary<Guid, BackgroundAudioTrack> _audioTracks = new();

    /// <param name="width">Ausgabebreite; Overlay-Positionen werden in diesem Pixelraum berechnet.</param>
    /// <param name="height">Ausgabehöhe.</param>
    public async Task<MediaComposition> BuildAsync(Timeline timeline, int width, int height)
    {
        var composition = new MediaComposition();
        var total = timeline.Duration;
        if (total <= TimeSpan.Zero) return composition;

        // --- V1 ---
        var cursor = TimeSpan.Zero;
        foreach (var c in timeline.GetClips(Timeline.MainTrack))
        {
            var asset = timeline.GetAsset(c.AssetId);
            AddGap(composition, c.Start - cursor);

            if (asset.Kind == MediaKind.Audio)
            {
                // Audio auf V1: Bild bleibt schwarz, Ton läuft als Hintergrundspur.
                AddGap(composition, c.Duration);
                composition.BackgroundAudioTracks.Add(await CreateAudioAsync(asset, c));
            }
            else
            {
                composition.Clips.Add(await CreateVideoClipAsync(asset, c));
            }
            cursor = c.End;
        }
        AddGap(composition, total - cursor);

        // --- V2 ---
        var layer = new MediaOverlayLayer();
        foreach (var c in timeline.GetClips(Timeline.OverlayTrack))
        {
            var asset = timeline.GetAsset(c.AssetId);
            if (asset.Kind == MediaKind.Audio)
            {
                composition.BackgroundAudioTracks.Add(await CreateAudioAsync(asset, c));
                continue;
            }

            var r = Geometry.AspectFitPixels(asset.Width, asset.Height, width, height);
            var overlay = new MediaOverlay(await CreateVideoClipAsync(asset, c))
            {
                Position = new Rect(r.X, r.Y, r.Width, r.Height),
                Delay = c.Start,
                Opacity = 1.0,
                AudioEnabled = asset.HasAudio && !c.AudioMuted,
            };
            layer.Overlays.Add(overlay);
        }
        if (layer.Overlays.Count > 0)
            composition.OverlayLayers.Add(layer);

        return composition;
    }

    /// <summary>Gibt zwischengespeicherte Dateien/Clips frei, z. B. nachdem ein Medium aus dem Pool entfernt wurde.</summary>
    public void Forget(Guid assetId)
    {
        _files.Remove(assetId);
        _videoClips.Remove(assetId);
        _audioTracks.Remove(assetId);
    }

    private static void AddGap(MediaComposition composition, TimeSpan duration)
    {
        if (duration >= TimeSpan.FromMilliseconds(1))
            composition.Clips.Add(MediaClip.CreateFromColor(Microsoft.UI.Colors.Black, duration));
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
