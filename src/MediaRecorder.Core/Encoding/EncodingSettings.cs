namespace MediaRecorder.Core.Encoding;

/// <summary>Ausgabecontainer. Nur Formate, die Media Foundation unter Windows nativ schreiben kann.</summary>
public enum ContainerFormat
{
    Mp4,
    Wmv,
}

public enum VideoCodec
{
    H264,
    Hevc,
    Wmv9,
}

public enum AudioCodec
{
    Aac,
    /// <summary>Verlustfrei. Annahme: im MP4-Sink erst ab Windows 10 1809 verfügbar, auf echten Systemen zu prüfen.</summary>
    Flac,
    /// <summary>Unkomprimiert. Annahme: wie FLAC, Unterstützung im MP4-Sink zur Laufzeit prüfen.</summary>
    Pcm,
    Wma,
}

/// <summary>
/// Gemeinsame Encoding-Einstellungen für Aufnahme (Seite 1) und Export (Seite 2).
/// Wird in der App über MediaRecorder.App.Services.EncodingProfileFactory auf ein
/// Windows.Media.MediaProperties.MediaEncodingProfile abgebildet.
/// Für Width, Height und FrameRate gilt: 0 = von der Quelle übernehmen.
/// </summary>
public sealed record EncodingSettings
{
    public ContainerFormat Container { get; init; } = ContainerFormat.Mp4;

    public VideoCodec VideoCodec { get; init; } = VideoCodec.H264;
    /// <summary>Breite in Pixeln, 0 = Quelle.</summary>
    public int Width { get; init; }
    /// <summary>Höhe in Pixeln, 0 = Quelle.</summary>
    public int Height { get; init; }
    /// <summary>Bilder pro Sekunde, 0 = Quelle. 29.97 und 59.94 werden als 30000/1001 bzw. 60000/1001 geschrieben.</summary>
    public double FrameRate { get; init; }
    /// <summary>Videobitrate in kbit/s.</summary>
    public int VideoBitrateKbps { get; init; } = 12_000;

    public AudioCodec AudioCodec { get; init; } = AudioCodec.Aac;
    public int AudioSampleRate { get; init; } = 48_000;
    public int AudioChannels { get; init; } = 2;
    public int AudioBitsPerSample { get; init; } = 16;
    /// <summary>Audiobitrate in kbit/s. Bei FLAC und PCM ignoriert.</summary>
    public int AudioBitrateKbps { get; init; } = 192;

    public bool IncludeVideo { get; init; } = true;
    public bool IncludeAudio { get; init; } = true;

    public bool HasExplicitResolution => Width > 0 && Height > 0;
    public bool HasExplicitFrameRate => FrameRate > 0;

    public string FileExtension => Container switch
    {
        ContainerFormat.Wmv => ".wmv",
        _ => ".mp4",
    };

    public static bool IsLossless(AudioCodec codec) => codec is AudioCodec.Flac or AudioCodec.Pcm;

    /// <summary>Wandelt eine Bildrate in einen exakten Bruch (NTSC-Raten als x000/1001).</summary>
    public static (uint Numerator, uint Denominator) ToFraction(double fps)
    {
        foreach (var ntsc in new[] { 24, 30, 60, 120 })
        {
            if (Math.Abs(fps - ntsc * 1000.0 / 1001.0) < 0.01)
                return ((uint)(ntsc * 1000), 1001);
        }
        if (Math.Abs(fps - Math.Round(fps)) < 0.001)
            return ((uint)Math.Round(fps), 1);
        return ((uint)Math.Round(fps * 1000), 1000);
    }

    public static IReadOnlyList<VideoCodec> VideoCodecsFor(ContainerFormat container) => container switch
    {
        ContainerFormat.Mp4 => [VideoCodec.H264, VideoCodec.Hevc],
        ContainerFormat.Wmv => [VideoCodec.Wmv9],
        _ => [],
    };

    /// <summary>
    /// Konservative Liste für den Export. FLAC/PCM im MP4 werden hier bewusst nicht angeboten, bis auf
    /// echten Systemen geprüft ist, dass der MP4-Sink sie schreibt (die Aufnahmeseite prüft das zur Laufzeit).
    /// </summary>
    public static IReadOnlyList<AudioCodec> AudioCodecsFor(ContainerFormat container) => container switch
    {
        ContainerFormat.Mp4 => [AudioCodec.Aac],
        ContainerFormat.Wmv => [AudioCodec.Wma],
        _ => [],
    };

    /// <summary>Prüft die Kombination und liefert verständliche Fehlermeldungen (leer = gültig).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!IncludeVideo && !IncludeAudio)
            errors.Add("Mindestens Video oder Audio muss enthalten sein.");

        if (IncludeVideo)
        {
            if (!VideoCodecsFor(Container).Contains(VideoCodec))
                errors.Add($"Videocodec {VideoCodec} wird im Container {Container} nicht unterstützt.");
            if ((Width == 0) != (Height == 0))
                errors.Add("Breite und Höhe müssen beide gesetzt oder beide 0 (Quelle) sein.");
            if (HasExplicitResolution)
            {
                if (Width < 16 || Height < 16 || Width > 7680 || Height > 4320)
                    errors.Add("Auflösung muss zwischen 16×16 und 7680×4320 liegen.");
                if (Width % 2 != 0 || Height % 2 != 0)
                    errors.Add("Breite und Höhe müssen gerade Zahlen sein (4:2:0-Farbunterabtastung).");
            }
            if (FrameRate < 0 || FrameRate > 240)
                errors.Add("Bildrate muss zwischen 0 (Quelle) und 240 liegen.");
            if (VideoBitrateKbps < 100 || VideoBitrateKbps > 400_000)
                errors.Add("Videobitrate muss zwischen 0,1 und 400 Mbit/s liegen.");
        }

        if (IncludeAudio)
        {
            if (AudioChannels is < 1 or > 8)
                errors.Add("Audiokanäle müssen zwischen 1 und 8 liegen.");
            if (AudioSampleRate is not (44_100 or 48_000 or 96_000))
                errors.Add("Abtastrate muss 44,1, 48 oder 96 kHz sein.");
            if (AudioCodec == AudioCodec.Aac)
            {
                if (AudioSampleRate == 96_000)
                    errors.Add("AAC unterstützt unter Windows nur 44,1 und 48 kHz.");
                if (AudioChannels is not (1 or 2 or 6))
                    errors.Add("AAC unterstützt unter Windows 1, 2 oder 6 Kanäle.");
            }
            if (!IsLossless(AudioCodec) && (AudioBitrateKbps < 32 || AudioBitrateKbps > 320))
                errors.Add("Audiobitrate muss zwischen 32 und 320 kbit/s liegen.");
            if (Container == ContainerFormat.Wmv && AudioCodec != AudioCodec.Wma)
                errors.Add("Im WMV-Container ist nur WMA-Audio möglich.");
            if (Container == ContainerFormat.Mp4 && AudioCodec == AudioCodec.Wma)
                errors.Add("WMA-Audio ist im MP4-Container nicht möglich.");
        }

        return errors;
    }
}
