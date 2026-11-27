namespace MediaRecorder.Core.Encoding;

public sealed record EncodingPreset(string Name, string Description, EncodingSettings Settings)
{
    public override string ToString() => Name;
}

/// <summary>Vorgaben für den Export. Änderungen an einzelnen Werten machen daraus in der UI "Benutzerdefiniert".</summary>
public static class EncodingPresets
{
    public static EncodingPreset Hd1080H264 { get; } = new(
        "1080p H.264 (Standard)", "1920×1080, 30 fps, 12 Mbit/s, AAC 192 kbit/s",
        new EncodingSettings { Width = 1920, Height = 1080, FrameRate = 30 });

    public static EncodingPreset Hd1080Hevc { get; } = new(
        "1080p HEVC (kleinere Datei)", "1920×1080, 30 fps, 8 Mbit/s, AAC 192 kbit/s",
        new EncodingSettings { VideoCodec = VideoCodec.Hevc, Width = 1920, Height = 1080, FrameRate = 30, VideoBitrateKbps = 8_000 });

    public static EncodingPreset Hd720H264 { get; } = new(
        "720p H.264 (Web)", "1280×720, 30 fps, 5 Mbit/s, AAC 160 kbit/s",
        new EncodingSettings { Width = 1280, Height = 720, FrameRate = 30, VideoBitrateKbps = 5_000, AudioBitrateKbps = 160 });

    public static EncodingPreset Uhd2160Hevc { get; } = new(
        "2160p HEVC (4K)", "3840×2160, 30 fps, 40 Mbit/s, AAC 256 kbit/s",
        new EncodingSettings { VideoCodec = VideoCodec.Hevc, Width = 3840, Height = 2160, FrameRate = 30, VideoBitrateKbps = 40_000, AudioBitrateKbps = 256 });

    public static EncodingPreset Archive1080 { get; } = new(
        "1080p hohe Qualität", "1920×1080, 30 fps, H.264 30 Mbit/s, AAC 320 kbit/s",
        new EncodingSettings { Width = 1920, Height = 1080, FrameRate = 30, VideoBitrateKbps = 30_000, AudioBitrateKbps = 320 });

    public static IReadOnlyList<EncodingPreset> All { get; } =
        [Hd1080H264, Hd1080Hevc, Hd720H264, Uhd2160Hevc, Archive1080];
}
