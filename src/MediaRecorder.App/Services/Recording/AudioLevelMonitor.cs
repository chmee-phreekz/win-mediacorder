using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MediaRecorder.Core.Audio;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Render;

namespace MediaRecorder.App.Services.Recording;

/// <summary>Pegel eines Kanals in dBFS (-∞ … 0).</summary>
public readonly record struct AudioLevels(double LeftPeakDb, double RightPeakDb, double LeftRmsDb, double RightRmsDb, int Channels, double GainReductionDb = 0);

/// <summary>
/// Misst den Pegel des gewählten Audioeingangs für die VU-Anzeige.
/// Eigenständig über AudioGraph (geteilter Modus), unabhängig von MediaCapture: die Aufnahme wird dadurch nicht beeinflusst,
/// und ein Fehler hier blendet nur die Anzeige aus.
/// </summary>
public sealed class AudioLevelMonitor : IAsyncDisposable
{
    public const double SilenceDb = -90;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AudioGraph? _graph;
    private AudioDeviceInputNode? _input;
    private AudioFrameOutputNode? _output;
    private int _channels;

    // Aufsummiert zwischen zwei Meldungen (Audio-Thread schreibt, wird pro Meldung zurückgesetzt).
    private float _peakL, _peakR;
    private double _sumSqL, _sumSqR;
    private long _samples;
    private long _lastReportTicks;

    /// <summary>Ca. 30 Meldungen pro Sekunde, aus einem Audio-Thread.</summary>
    public event EventHandler<AudioLevels>? LevelsAvailable;

    public bool IsRunning => _graph is not null;

    /// <summary>
    /// Gleicher Kompressor (gleicher Algorithmus, gleiche Einstellungen) wie im Aufnahmepfad.
    /// Die Anzeige zeigt damit sofort das Signal, wie es in der Datei landet.
    /// </summary>
    public Compressor Compressor { get; } = new();

    public async Task StartAsync(string? audioDeviceId)
    {
        await _gate.WaitAsync();
        try
        {
            StopInternal();
            if (string.IsNullOrEmpty(audioDeviceId)) return;

            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.SystemDefault,
            };
            var graphResult = await AudioGraph.CreateAsync(settings);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException($"AudioGraph konnte nicht erstellt werden ({graphResult.Status}).");
            var graph = graphResult.Graph;

            var device = await DeviceInformation.CreateFromIdAsync(audioDeviceId);
            var inputResult = await graph.CreateDeviceInputNodeAsync(MediaCategory.Media, graph.EncodingProperties, device);
            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                graph.Dispose();
                throw new InvalidOperationException($"Audioeingang für Pegelanzeige nicht verfügbar ({inputResult.Status}).");
            }

            var output = graph.CreateFrameOutputNode();
            inputResult.DeviceInputNode.AddOutgoingConnection(output);

            _graph = graph;
            _input = inputResult.DeviceInputNode;
            _output = output;
            _channels = (int)Math.Max(1, graph.EncodingProperties.ChannelCount);
            Compressor.SetSampleRate((int)Math.Max(8000, graph.EncodingProperties.SampleRate));
            Compressor.Reset();
            ResetAccumulators();

            graph.QuantumStarted += OnQuantumStarted;
            graph.UnrecoverableErrorOccurred += OnGraphError;
            graph.Start();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { StopInternal(); }
        finally { _gate.Release(); }
    }

    private void StopInternal()
    {
        var graph = _graph;
        _graph = null;
        if (graph is null) return;
        try
        {
            graph.QuantumStarted -= OnQuantumStarted;
            graph.UnrecoverableErrorOccurred -= OnGraphError;
            graph.Stop();
        }
        catch { }
        try { _input?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { graph.Dispose(); } catch { }
        _input = null;
        _output = null;
        LevelsAvailable?.Invoke(this, new AudioLevels(SilenceDb, SilenceDb, SilenceDb, SilenceDb, 0));
    }

    private void OnGraphError(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
    {
        ErrorReporter.Log(null, $"AudioGraph-Fehler (Pegelanzeige): {args.Error}");
        _ = StopAsync();
    }

    private void OnQuantumStarted(AudioGraph sender, object args)
    {
        try
        {
            var output = _output;
            if (output is null) return;
            using var frame = output.GetFrame();
            Accumulate(frame);

            var now = Environment.TickCount64;
            if (now - _lastReportTicks < 33) return;
            _lastReportTicks = now;

            var levels = new AudioLevels(
                ToDb(_peakL), ToDb(_channels > 1 ? _peakR : _peakL),
                ToDb(Math.Sqrt(_sumSqL / Math.Max(1, _samples))),
                ToDb(Math.Sqrt((_channels > 1 ? _sumSqR : _sumSqL) / Math.Max(1, _samples))),
                _channels,
                Compressor.GainReductionDb);
            ResetAccumulators();
            LevelsAvailable?.Invoke(this, levels);
        }
        catch (Exception ex)
        {
            // Nie aus dem Audio-Thread werfen.
            ErrorReporter.Log(ex, "Pegelmessung");
        }
    }

    private unsafe void Accumulate(AudioFrame frame)
    {
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.ReadWrite);
        using var reference = buffer.CreateReference();
        var access = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference);
        access.GetBuffer(out byte* data, out uint capacity);

        // AudioGraph arbeitet intern immer mit 32-Bit-Float, verschachtelt (L R L R ...).
        var samples = (float*)data;
        int count = (int)(Math.Min(buffer.Length, capacity) / sizeof(float));
        int ch = _channels;
        // Erst komprimieren (Kopie des Signals, die Aufnahme ist davon unabhängig), dann messen.
        Compressor.Process(new Span<float>(samples, count), ch);
        for (int i = 0; i + ch - 1 < count; i += ch)
        {
            float l = samples[i];
            float r = ch > 1 ? samples[i + 1] : l;
            float al = Math.Abs(l), ar = Math.Abs(r);
            if (al > _peakL) _peakL = al;
            if (ar > _peakR) _peakR = ar;
            _sumSqL += l * l;
            _sumSqR += r * r;
            _samples++;
        }
    }

    private void ResetAccumulators()
    {
        _peakL = _peakR = 0;
        _sumSqL = _sumSqR = 0;
        _samples = 0;
    }

    private static double ToDb(double linear) =>
        linear <= 0.0000316 ? SilenceDb : Math.Max(SilenceDb, 20 * Math.Log10(linear));

    public async ValueTask DisposeAsync() => await StopAsync();

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
