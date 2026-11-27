namespace MediaRecorder.Core.Devices;

public enum CaptureDeviceKind
{
    Video,
    Audio
}

/// <summary>Ein Aufnahmegerät (Webcam, Grabber, Soundkarte, USB-Audio ...), UI-neutral.</summary>
public sealed record CaptureDeviceInfo(string Id, string Name, CaptureDeviceKind Kind, bool IsDefault = false)
{
    public override string ToString() => IsDefault ? $"{Name} (Standard)" : Name;
}

/// <summary>Ein vom Gerät angebotenes Videoformat.</summary>
public sealed record VideoCaptureFormat(uint Width, uint Height, double FrameRate, string Subtype)
{
    public string Resolution => $"{Width}×{Height}";

    /// <summary>Komprimierte Quellformate (MJPG, H264) belasten die CPU beim Dekodieren, liefern aber höhere Auflösungen über USB 2.</summary>
    public bool IsCompressed => Subtype is "MJPG" or "H264" or "HEVC";

    public override string ToString() => $"{Width}×{Height} @ {FrameRate:0.##} fps ({Subtype})";
}
