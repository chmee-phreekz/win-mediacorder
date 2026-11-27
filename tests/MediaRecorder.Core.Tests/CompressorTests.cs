using MediaRecorder.Core.Audio;

namespace MediaRecorder.Core.Tests;

public class CompressorTests
{
    [Theory]
    [InlineData(-30, -18, 4, 0)]          // unter der Schwelle: nichts
    [InlineData(-6, -18, 4, -9)]          // 12 dB drüber, 4:1 -> 3 dB drüber -> -9 dB
    [InlineData(0, -20, 2, -10)]          // 20 dB drüber, 2:1 -> -10 dB
    [InlineData(-6, -18, 1, 0)]           // Ratio 1 = aus
    public void StaticCurve_HardKnee(double input, double threshold, double ratio, double expected)
    {
        Assert.Equal(expected, Compressor.ComputeGainReductionDb(input, threshold, ratio, kneeDb: 0), 6);
    }

    [Fact]
    public void SoftKnee_IsContinuousAtEdges()
    {
        const double t = -18, r = 4, k = 6;
        Assert.Equal(0, Compressor.ComputeGainReductionDb(t - k / 2, t, r, k), 6);
        var hard = Compressor.ComputeGainReductionDb(t + k / 2, t, r, 0);
        Assert.Equal(hard, Compressor.ComputeGainReductionDb(t + k / 2, t, r, k), 6);
    }

    [Fact]
    public void Disabled_LeavesSamplesUntouched()
    {
        var c = new Compressor(48000) { Settings = new CompressorSettings { Enabled = false, GainDb = 12 } };
        var buf = new float[] { 0.5f, -0.5f, 0.25f, 0.1f };
        c.Process(buf, 2);
        Assert.Equal(new[] { 0.5f, -0.5f, 0.25f, 0.1f }, buf);
    }

    [Fact]
    public void LoudSine_IsReduced_AndGainApplied()
    {
        var c = new Compressor(48000) { Settings = new CompressorSettings { Enabled = true, ThresholdDb = -20, Ratio = 4, GainDb = 0 } };
        var buf = Sine(48000, 1.0f, seconds: 1, channels: 2); // 0 dBFS
        c.Process(buf, 2);
        // Letzte 100 ms: eingeschwungen, Spitze deutlich unter 0 dBFS
        float peak = buf.Skip(buf.Length - 9600).Max(x => Math.Abs(x));
        Assert.InRange(20 * Math.Log10(peak), -17.0, -12.0); // ca. -15 dB (20 drüber /4 = 5 drüber)
        Assert.True(c.GainReductionDb < -10);
    }

    [Fact]
    public void MakeupGain_NeverClipsBeyondFullScale()
    {
        var c = new Compressor(48000) { Settings = new CompressorSettings { Enabled = true, ThresholdDb = 0, Ratio = 1, GainDb = 24 } };
        var buf = Sine(48000, 0.5f, seconds: 0.1, channels: 1);
        c.Process(buf, 1);
        Assert.All(buf, v => Assert.InRange(v, -1f, 1f));
    }

    [Fact]
    public void Settings_AreClamped()
    {
        var c = new Compressor { Settings = new CompressorSettings { ThresholdDb = -200, Ratio = 0, GainDb = 99 } };
        Assert.Equal(CompressorSettings.MinThresholdDb, c.Settings.ThresholdDb);
        Assert.Equal(CompressorSettings.MinRatio, c.Settings.Ratio);
        Assert.Equal(CompressorSettings.MaxGainDb, c.Settings.GainDb);
    }

    private static float[] Sine(int rate, float amp, double seconds, int channels)
    {
        int n = (int)(rate * seconds);
        var buf = new float[n * channels];
        for (int i = 0; i < n; i++)
        {
            float v = amp * (float)Math.Sin(2 * Math.PI * 1000 * i / rate);
            for (int c = 0; c < channels; c++) buf[i * channels + c] = v;
        }
        return buf;
    }
}
