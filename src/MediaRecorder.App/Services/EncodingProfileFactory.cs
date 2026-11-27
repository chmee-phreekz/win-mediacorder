using System;
using MediaRecorder.Core.Encoding;
using Windows.Media.MediaProperties;

namespace MediaRecorder.App.Services;

/// <summary>
/// Einzige Stelle, die gemeinsame <see cref="EncodingSettings"/> in ein Media-Foundation-Profil übersetzt.
/// Wird von der Aufnahme (MediaCapture) und vom Export (MediaComposition) verwendet.
/// </summary>
public static class EncodingProfileFactory
{
    public static MediaEncodingProfile Create(EncodingSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!s.IncludeVideo && !s.IncludeAudio)
            throw new ArgumentException("Mindestens Video oder Audio muss enthalten sein.", nameof(s));
        if (s.Container == ContainerFormat.Wmv && s.VideoCodec != VideoCodec.Wmv9 && s.IncludeVideo)
            throw new ArgumentException("Im WMV-Container ist nur der WMV9-Videocodec möglich.", nameof(s));
        if (s.Container == ContainerFormat.Mp4 && s.VideoCodec == VideoCodec.Wmv9 && s.IncludeVideo)
            throw new ArgumentException("WMV9 ist im MP4-Container nicht möglich.", nameof(s));

        // "Auto" übernimmt Auflösung und Bildrate von der Quelle; explizite Werte überschreiben das unten.
        var profile = (s.Container, s.VideoCodec) switch
        {
            (ContainerFormat.Wmv, _) => MediaEncodingProfile.CreateWmv(VideoEncodingQuality.Auto),
            (_, VideoCodec.Hevc) => MediaEncodingProfile.CreateHevc(VideoEncodingQuality.Auto),
            _ => MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto),
        };

        if (s.IncludeVideo)
        {
            var v = profile.Video;
            v.Bitrate = checked((uint)s.VideoBitrateKbps * 1000u);
            if (s.HasExplicitResolution)
            {
                v.Width = (uint)s.Width;
                v.Height = (uint)s.Height;
            }
            if (s.HasExplicitFrameRate)
            {
                var (num, den) = EncodingSettings.ToFraction(s.FrameRate);
                v.FrameRate.Numerator = num;
                v.FrameRate.Denominator = den;
            }
            // Quadratische Pixel: verhindert verzerrte Ausgabe bei Quellen mit anamorphem Seitenverhältnis.
            v.PixelAspectRatio.Numerator = 1;
            v.PixelAspectRatio.Denominator = 1;
        }
        else
        {
            profile.Video = null;
        }

        if (s.IncludeAudio)
        {
            uint rate = (uint)s.AudioSampleRate, ch = (uint)s.AudioChannels, bps = (uint)s.AudioBitsPerSample;
            uint bitrate = (uint)s.AudioBitrateKbps * 1000u;
            profile.Audio = s.AudioCodec switch
            {
                AudioCodec.Aac => AudioEncodingProperties.CreateAac(rate, ch, bitrate),
                AudioCodec.Flac => AudioEncodingProperties.CreateFlac(rate, ch, bps),
                AudioCodec.Pcm => AudioEncodingProperties.CreatePcm(rate, ch, bps),
                AudioCodec.Wma => AudioEncodingProperties.CreateWma(rate, ch, bitrate),
                _ => throw new ArgumentOutOfRangeException(nameof(s), s.AudioCodec, "Unbekannter Audiocodec."),
            };
        }
        else
        {
            profile.Audio = null;
        }

        return profile;
    }
}
