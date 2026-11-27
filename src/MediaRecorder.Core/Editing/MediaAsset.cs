namespace MediaRecorder.Core.Editing;

public enum MediaKind
{
    Video,
    Audio,
    Image,
}

/// <summary>Eine importierte Mediendatei (Eintrag im Medienpool). Unveränderlich.</summary>
public sealed record MediaAsset
{
    /// <summary>Standardlänge, mit der ein Standbild auf die Timeline gelegt wird.</summary>
    public static readonly TimeSpan DefaultImageDuration = TimeSpan.FromSeconds(5);
    /// <summary>Maximale Länge, auf die ein Standbild aufgezogen werden kann.</summary>
    public static readonly TimeSpan MaxImageDuration = TimeSpan.FromHours(1);

    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Path { get; init; }
    public required MediaKind Kind { get; init; }
    /// <summary>Länge der Quelle. Bei Bildern die Standardlänge.</summary>
    public required TimeSpan Duration { get; init; }
    /// <summary>Anzeigebreite in Pixeln (Pixel-Seitenverhältnis bereits eingerechnet). 0 bei reinem Audio.</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public bool HasAudio { get; init; }

    public string DisplayName => System.IO.Path.GetFileName(Path);
    public bool HasVideo => Kind is MediaKind.Video or MediaKind.Image;

    /// <summary>Breite/Höhe, bei fehlenden Angaben 16:9.</summary>
    public double AspectRatio => Width > 0 && Height > 0 ? Width / (double)Height : 16.0 / 9.0;

    /// <summary>Wie weit ein Clip dieser Quelle maximal reichen darf.</summary>
    public TimeSpan MaxSourceDuration => Kind == MediaKind.Image ? MaxImageDuration : Duration;
}
