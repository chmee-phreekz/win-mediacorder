using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MediaRecorder.Core.Video;
using Windows.Foundation.Collections;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;

namespace MediaRecorder.Effects;

/// <summary>
/// Wendet eine 3D-LUT (.cube) im MediaCapture-Aufnahmepfad auf das Videosignal an (NV12, CPU, mehrkernig).
/// Parameter über das PropertySet: Enabled (bool), LutPath (string), Version (int, bei jeder Änderung erhöhen).
/// Rückmeldung: LutError (string, leer = ok).
/// </summary>
public sealed class LutEffect : IBasicVideoEffect
{
    private IPropertySet? _props;
    private int _appliedVersion = -1;
    private volatile Nv12LutTables? _tables;
    private volatile bool _enabled;
    private string? _requestedPath;
    private CubeLut? _lut;
    private YuvMatrix _matrix = YuvMatrix.Bt709;
    private int _loadGeneration;

    public bool IsReadOnly => false;
    public MediaMemoryTypes SupportedMemoryTypes => MediaMemoryTypes.Cpu;
    public bool TimeIndependent => true;

    public IReadOnlyList<VideoEncodingProperties> SupportedEncodingProperties =>
        new List<VideoEncodingProperties> { new VideoEncodingProperties { Subtype = MediaEncodingSubtypes.Nv12 } };

    public void SetEncodingProperties(VideoEncodingProperties encodingProperties, IDirect3DDevice device)
    {
        // HD und größer: BT.709, darunter BT.601 (übliche Kamerakonvention).
        var matrix = encodingProperties.Height >= 720 ? YuvMatrix.Bt709 : YuvMatrix.Bt601;
        if (matrix != _matrix || _tables is null)
        {
            _matrix = matrix;
            if (_lut is not null) BuildTablesAsync(_lut);
        }
    }

    public void SetProperties(IPropertySet configuration)
    {
        _props = configuration;
        _appliedVersion = -1;
        ApplySettingsIfChanged();
    }

    public void ProcessFrame(ProcessVideoFrameContext context)
    {
        var input = context.InputFrame.SoftwareBitmap;
        var output = context.OutputFrame.SoftwareBitmap;
        try
        {
            ApplySettingsIfChanged();
            var tables = _tables;
            if (!_enabled || tables is null || input is null || output is null
                || input.BitmapPixelFormat != BitmapPixelFormat.Nv12 || output.BitmapPixelFormat != BitmapPixelFormat.Nv12)
            {
                input?.CopyTo(output);
                return;
            }
            Apply(tables, input, output);
        }
        catch
        {
            // Nie die Aufnahme abbrechen: im Zweifel Bild unverändert durchreichen.
            try { input?.CopyTo(output); } catch { }
        }
    }

    private static unsafe void Apply(Nv12LutTables tables, SoftwareBitmap input, SoftwareBitmap output)
    {
        using var inBuf = input.LockBuffer(BitmapBufferAccessMode.Read);
        using var outBuf = output.LockBuffer(BitmapBufferAccessMode.Write);
        var inY = inBuf.GetPlaneDescription(0); var inUv = inBuf.GetPlaneDescription(1);
        var outY = outBuf.GetPlaneDescription(0); var outUv = outBuf.GetPlaneDescription(1);

        using var inRef = inBuf.CreateReference();
        using var outRef = outBuf.CreateReference();
        WinRT.CastExtensions.As<IMemoryBufferByteAccess>(inRef).GetBuffer(out byte* inData, out uint inCap);
        WinRT.CastExtensions.As<IMemoryBufferByteAccess>(outRef).GetBuffer(out byte* outData, out uint outCap);

        nint inBase = (nint)inData, outBase = (nint)outData;
        int inLen = (int)inCap, outLen = (int)outCap;
        int width = Math.Min(inY.Width, outY.Width) & ~1;
        int height = Math.Min(inY.Height, outY.Height);
        int pairs = height / 2;

        Parallel.For(0, pairs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, p =>
        {
            var src = new ReadOnlySpan<byte>((void*)inBase, inLen);
            var dst = new Span<byte>((void*)outBase, outLen);
            tables.ApplyRowPair(
                src[inY.StartIndex..], inY.Stride, src[inUv.StartIndex..], inUv.Stride,
                dst[outY.StartIndex..], outY.Stride, dst[outUv.StartIndex..], outUv.Stride,
                width, p);
        });
    }

    private void ApplySettingsIfChanged()
    {
        var p = _props;
        if (p is null) return;
        int version = p.TryGetValue("Version", out var v) && v is int i ? i : 0;
        if (version == _appliedVersion) return;
        _appliedVersion = version;

        _enabled = p.TryGetValue("Enabled", out var e) && e is bool b && b;
        var path = p.TryGetValue("LutPath", out var lp) ? lp as string : null;
        if (!string.Equals(path, _requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            _requestedPath = path;
            LoadAsync(path);
        }
    }

    private void LoadAsync(string? path)
    {
        int gen = ++_loadGeneration;
        if (string.IsNullOrEmpty(path))
        {
            _lut = null;
            _tables = null;
            SetError("");
            return;
        }
        // Laden und Tabellen bauen nicht im Media-Foundation-Thread: bis dahin läuft das Bild unverändert durch.
        Task.Run(() =>
        {
            try
            {
                var lut = CubeLut.Load(path);
                if (gen != _loadGeneration) return;
                _lut = lut;
                var tables = new Nv12LutTables(lut, _matrix);
                if (gen != _loadGeneration) return;
                _tables = tables;
                SetError("");
            }
            catch (Exception ex)
            {
                if (gen == _loadGeneration) { _tables = null; SetError(ex.Message); }
            }
        });
    }

    private void BuildTablesAsync(CubeLut lut)
    {
        int gen = _loadGeneration;
        var matrix = _matrix;
        Task.Run(() =>
        {
            try
            {
                var t = new Nv12LutTables(lut, matrix);
                if (gen == _loadGeneration) _tables = t;
            }
            catch (Exception ex) { SetError(ex.Message); }
        });
    }

    private void SetError(string message)
    {
        try { if (_props is not null) _props["LutError"] = message; } catch { }
    }

    public void Close(MediaEffectClosedReason reason)
    {
        _loadGeneration++;
        _tables = null;
    }

    public void DiscardQueuedFrames() { }
}
