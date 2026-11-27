namespace MediaRecorder.Core.Audio;

/// <summary>Benutzer-Einstellungen des Kompressors (werden mit den Aufnahme-Einstellungen gespeichert).</summary>
public sealed record CompressorSettings
{
    public bool Enabled { get; init; }
    /// <summary>Schwelle in dBFS, ab der komprimiert wird (-60 … 0).</summary>
    public double ThresholdDb { get; init; } = -18;
    /// <summary>Verhältnis n:1 (1 = aus, 20 ≈ Limiter).</summary>
    public double Ratio { get; init; } = 3;
    /// <summary>Aufholverstärkung (Make-up-Gain) in dB (-12 … +24).</summary>
    public double GainDb { get; init; }

    public const double MinThresholdDb = -60, MaxThresholdDb = 0;
    public const double MinRatio = 1, MaxRatio = 20;
    public const double MinGainDb = -12, MaxGainDb = 24;

    public CompressorSettings Clamped() => this with
    {
        ThresholdDb = Math.Clamp(ThresholdDb, MinThresholdDb, MaxThresholdDb),
        Ratio = Math.Clamp(Ratio, MinRatio, MaxRatio),
        GainDb = Math.Clamp(GainDb, MinGainDb, MaxGainDb),
    };
}

/// <summary>
/// Stereo-gekoppelter Feed-forward-Kompressor (Spitzenwert-Detektor, Soft-Knee) für 32-Bit-Float-Samples.
/// Echtzeitfähig: keine Speicheranforderungen in Process, Parameter können jederzeit aus einem anderen Thread
/// geändert werden (wirken ab dem nächsten Puffer).
/// </summary>
public sealed class Compressor
{
    // ---- Nur im Code verstellbar (Vorgabe) ----
    /// <summary>Ansprechzeit in Millisekunden.</summary>
    public const double AttackMs = 10;
    /// <summary>Rücklaufzeit in Millisekunden.</summary>
    public const double ReleaseMs = 150;
    /// <summary>Breite des weichen Übergangs um die Schwelle in dB (0 = harter Knick).</summary>
    public const double KneeDb = 6;
    /// <summary>Ausgang hart auf ±1,0 begrenzen, damit Make-up-Gain nie digital übersteuert.</summary>
    public const bool OutputSafetyClip = true;

    private volatile CompressorSettings _settings = new();
    private double _envelopeDb = -120;
    private double _attackCoef, _releaseCoef;
    private int _sampleRate;

    /// <summary>Aktuelle Pegelreduktion in dB (≤ 0), z.B. für eine Anzeige.</summary>
    public double GainReductionDb { get; private set; }

    public CompressorSettings Settings
    {
        get => _settings;
        set => _settings = (value ?? new CompressorSettings()).Clamped();
    }

    public Compressor(int sampleRate = 48_000) => SetSampleRate(sampleRate);

    public void SetSampleRate(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        _attackCoef = Math.Exp(-1.0 / (AttackMs * 0.001 * sampleRate));
        _releaseCoef = Math.Exp(-1.0 / (ReleaseMs * 0.001 * sampleRate));
    }

    public int SampleRate => _sampleRate;

    public void Reset()
    {
        _envelopeDb = -120;
        GainReductionDb = 0;
    }

    /// <summary>Statische Kennlinie: Pegelreduktion in dB für einen Eingangspegel (≤ 0).</summary>
    public static double ComputeGainReductionDb(double inputDb, double thresholdDb, double ratio, double kneeDb = KneeDb)
    {
        if (ratio <= 1) return 0;
        double over = inputDb - thresholdDb;
        double slope = 1.0 / ratio - 1.0; // negativ
        if (kneeDb > 0 && Math.Abs(over) <= kneeDb / 2)
        {
            double x = over + kneeDb / 2;
            return slope * x * x / (2 * kneeDb);
        }
        return over > 0 ? slope * over : 0;
    }

    /// <summary>Bearbeitet verschachtelte Samples (L R L R …) an Ort und Stelle.</summary>
    public void Process(Span<float> interleaved, int channels)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        var s = _settings;
        if (!s.Enabled) { GainReductionDb = 0; return; }

        double makeup = s.GainDb;
        double env = _envelopeDb;
        double grMin = 0;

        for (int i = 0; i + channels <= interleaved.Length; i += channels)
        {
            // Stereo-gekoppelt: lautester Kanal steuert, damit das Stereobild nicht wandert.
            float peak = 0;
            for (int c = 0; c < channels; c++)
            {
                float a = Math.Abs(interleaved[i + c]);
                if (a > peak) peak = a;
            }
            double inDb = peak > 1e-6f ? 20 * Math.Log10(peak) : -120;

            double coef = inDb > env ? _attackCoef : _releaseCoef;
            env = coef * env + (1 - coef) * inDb;

            double gr = ComputeGainReductionDb(env, s.ThresholdDb, s.Ratio);
            if (gr < grMin) grMin = gr;
            float gain = (float)Math.Pow(10, (gr + makeup) / 20);

            for (int c = 0; c < channels; c++)
            {
                float v = interleaved[i + c] * gain;
                if (OutputSafetyClip) v = Math.Clamp(v, -1f, 1f);
                interleaved[i + c] = v;
            }
        }

        _envelopeDb = env;
        GainReductionDb = grMin;
    }
}
