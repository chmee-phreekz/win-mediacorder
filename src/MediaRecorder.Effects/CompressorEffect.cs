using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MediaRecorder.Core.Audio;
using Windows.Foundation.Collections;
using Windows.Media;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;

namespace MediaRecorder.Effects;

/// <summary>
/// Kompressor als Audioeffekt im MediaCapture-Aufnahmepfad (wirkt auf die aufgenommene Datei).
/// Die Parameter kommen über das PropertySet, das die App bei AddAudioEffectAsync übergibt, und können
/// während der Aufnahme geändert werden (Schlüssel siehe <see cref="Keys"/>).
/// </summary>
public sealed class CompressorEffect : IBasicAudioEffect
{
    private readonly Compressor _compressor = new();
    private IPropertySet? _props;
    private int _appliedVersion = -1;
    private int _channels = 2;

    public IReadOnlyList<AudioEncodingProperties> SupportedEncodingProperties
    {
        get
        {
            // 32-Bit-Float; Media Foundation setzt bei Bedarf einen Konverter davor.
            var list = new List<AudioEncodingProperties>();
            foreach (uint rate in new uint[] { 48000, 44100, 96000 })
            foreach (uint ch in new uint[] { 2, 1 })
            {
                var p = AudioEncodingProperties.CreatePcm(rate, ch, 32);
                p.Subtype = MediaEncodingSubtypes.Float;
                list.Add(p);
            }
            return list;
        }
    }

    public bool UseInputFrameForOutput => true;
    public bool TimeIndependent => true;

    public void SetEncodingProperties(AudioEncodingProperties encodingProperties)
    {
        _channels = (int)Math.Max(1, encodingProperties.ChannelCount);
        _compressor.SetSampleRate((int)Math.Max(8000, encodingProperties.SampleRate));
        _compressor.Reset();
    }

    public void SetProperties(IPropertySet configuration)
    {
        _props = configuration;
        _appliedVersion = -1;
        ApplySettingsIfChanged();
    }

    public void ProcessFrame(ProcessAudioFrameContext context)
    {
        try
        {
            ApplySettingsIfChanged();
            if (!_compressor.Settings.Enabled) return;
            Process(context.InputFrame);
            if (_props is not null) _props[Keys.GainReductionDb] = _compressor.GainReductionDb;
        }
        catch
        {
            // Nie aus dem Media-Foundation-Thread werfen: lieber unkomprimiert weiter aufnehmen.
        }
    }

    private unsafe void Process(AudioFrame frame)
    {
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.ReadWrite);
        using var reference = buffer.CreateReference();
        var access = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference);
        access.GetBuffer(out byte* data, out uint capacity);
        int count = (int)(Math.Min(buffer.Length, capacity) / sizeof(float));
        _compressor.Process(new Span<float>(data, count), _channels);
    }

    private void ApplySettingsIfChanged()
    {
        var p = _props;
        if (p is null) return;
        int version = p.TryGetValue(Keys.Version, out var v) && v is int i ? i : 0;
        if (version == _appliedVersion) return;
        _appliedVersion = version;
        _compressor.Settings = new CompressorSettings
        {
            Enabled = p.TryGetValue(Keys.Enabled, out var e) && e is bool b && b,
            ThresholdDb = Get(p, Keys.ThresholdDb, -18),
            Ratio = Get(p, Keys.Ratio, 3),
            GainDb = Get(p, Keys.GainDb, 0),
        };
    }

    private static double Get(IPropertySet p, string key, double fallback) =>
        p.TryGetValue(key, out var v) && v is double d ? d : fallback;

    public void Close(MediaEffectClosedReason reason) => _compressor.Reset();

    public void DiscardQueuedFrames() => _compressor.Reset();
}

/// <summary>Schlüssel im PropertySet. Gleichlautend in MediaRecorder.App (MediaCaptureRecordingBackend).</summary>
internal static class Keys
{
    public const string Enabled = "Enabled";
    public const string ThresholdDb = "ThresholdDb";
    public const string Ratio = "Ratio";
    public const string GainDb = "GainDb";
    public const string Version = "Version";
    public const string GainReductionDb = "GainReductionDb";
}

[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
