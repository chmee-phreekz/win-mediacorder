using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaRecorder.Core.Editing;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace MediaRecorder.App.Services.Editing;

/// <summary>
/// Liest Mediendateien ein und ermittelt Länge, Anzeigegröße (inkl. Pixel-Seitenverhältnis und Drehung) und Audio.
/// </summary>
public sealed class MediaImportService
{
    public static readonly string[] VideoExtensions = [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".mts", ".m2ts"];
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac"];
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public static IEnumerable<string> AllExtensions => VideoExtensions.Concat(AudioExtensions).Concat(ImageExtensions);

    public static MediaKind? KindFromPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (VideoExtensions.Contains(ext)) return MediaKind.Video;
        if (AudioExtensions.Contains(ext)) return MediaKind.Audio;
        if (ImageExtensions.Contains(ext)) return MediaKind.Image;
        return null;
    }

    /// <summary>Liest eine Datei ein. Wirft <see cref="MediaImportException"/> mit verständlicher Meldung.</summary>
    public async Task<MediaAsset> ImportAsync(StorageFile file)
    {
        var kind = KindFromPath(file.Path)
            ?? throw new MediaImportException($"„{file.Name}“: Dateityp wird nicht unterstützt.");
        try
        {
            return kind switch
            {
                MediaKind.Video => await ProbeVideoAsync(file),
                MediaKind.Audio => await ProbeAudioAsync(file),
                _ => await ProbeImageAsync(file),
            };
        }
        catch (MediaImportException) { throw; }
        catch (Exception ex)
        {
            throw new MediaImportException($"„{file.Name}“ konnte nicht gelesen werden: {ex.Message}", ex);
        }
    }

    private static async Task<MediaAsset> ProbeVideoAsync(StorageFile file)
    {
        // MediaClip prüft gleichzeitig, ob Media Foundation die Datei für den Schnitt dekodieren kann.
        var clip = await MediaClip.CreateFromFileAsync(file);
        var enc = clip.GetVideoEncodingProperties();
        var props = await file.Properties.GetVideoPropertiesAsync();

        double w = enc.Width, h = enc.Height;
        // Anamorphe Quellen (z. B. DV/HDV): Anzeigebreite = Speicherbreite × Pixel-Seitenverhältnis.
        if (enc.PixelAspectRatio is { Numerator: > 0, Denominator: > 0 } par && par.Numerator != par.Denominator)
            w = w * par.Numerator / par.Denominator;
        // Hochkant-Videos vom Smartphone sind oft gedreht gespeichert.
        if (props.Orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270)
            (w, h) = (h, w);

        double fps = enc.FrameRate is { Numerator: > 0, Denominator: > 0 } fr ? fr.Numerator / (double)fr.Denominator : 30;

        if (clip.OriginalDuration <= TimeSpan.Zero)
            throw new MediaImportException($"„{file.Name}“ hat keine lesbare Länge.");

        return new MediaAsset
        {
            Path = file.Path,
            Kind = MediaKind.Video,
            Duration = clip.OriginalDuration,
            Width = (int)Math.Round(w),
            Height = (int)Math.Round(h),
            FrameRate = fps,
            HasAudio = clip.EmbeddedAudioTracks.Count > 0,
        };
    }

    private static async Task<MediaAsset> ProbeAudioAsync(StorageFile file)
    {
        var track = await BackgroundAudioTrack.CreateFromFileAsync(file);
        if (track.OriginalDuration <= TimeSpan.Zero)
            throw new MediaImportException($"„{file.Name}“ hat keine lesbare Länge.");
        return new MediaAsset
        {
            Path = file.Path,
            Kind = MediaKind.Audio,
            Duration = track.OriginalDuration,
            HasAudio = true,
        };
    }

    private static async Task<MediaAsset> ProbeImageAsync(StorageFile file)
    {
        ImageProperties props = await file.Properties.GetImagePropertiesAsync();
        uint w = props.Width, h = props.Height;
        if (props.Orientation is PhotoOrientation.Rotate90 or PhotoOrientation.Rotate270
            or PhotoOrientation.Transpose or PhotoOrientation.Transverse)
            (w, h) = (h, w);
        if (w == 0 || h == 0)
            throw new MediaImportException($"„{file.Name}“: Bildgröße unbekannt.");
        return new MediaAsset
        {
            Path = file.Path,
            Kind = MediaKind.Image,
            Duration = MediaAsset.DefaultImageDuration,
            Width = (int)w,
            Height = (int)h,
        };
    }
}

public sealed class MediaImportException : Exception
{
    public MediaImportException(string message, Exception? inner = null) : base(message, inner) { }
}
