using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MediaRecorder.Core.Video;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace MediaRecorder.App.Services.Recording;

/// <summary>
/// Vorschau mit LUT: liest Bilder per MediaFrameReader (BGRA8, auf max. 1280 px Breite verkleinert),
/// wendet die LUT an und liefert fertige SoftwareBitmaps für ein Image-Element.
/// Läuft nur, solange eine LUT aktiv ist; sonst nutzt die Vorschau den schnelleren MediaPlayer-Weg.
/// </summary>
public sealed class LutPreviewRenderer : IAsyncDisposable
{
    private readonly MediaFrameReader _reader;
    private volatile BgraLutTable? _table;
    private int _busy;

    /// <summary>Neues Bild (Hintergrund-Thread). Der Empfänger übernimmt das Bitmap und muss es freigeben.</summary>
    public event EventHandler<SoftwareBitmap>? FrameReady;

    private LutPreviewRenderer(MediaFrameReader reader, BgraLutTable table)
    {
        _reader = reader;
        _table = table;
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;
    }

    public static async Task<LutPreviewRenderer> StartAsync(MediaCapture capture, MediaFrameSource source,
                                                            uint sourceWidth, uint sourceHeight, BgraLutTable table)
    {
        uint w = Math.Min(1280u, sourceWidth == 0 ? 1280u : sourceWidth);
        uint h = sourceWidth == 0 ? 720u : (uint)Math.Round(w * (double)sourceHeight / sourceWidth);
        h &= ~1u;
        var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8, new BitmapSize { Width = w, Height = h });
        var renderer = new LutPreviewRenderer(reader, table);
        var status = await reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            await renderer.DisposeAsync();
            throw new InvalidOperationException($"LUT-Vorschau konnte nicht starten ({status}).");
        }
        return renderer;
    }

    public void SetTable(BgraLutTable table) => _table = table;

    private async void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // Ist das vorige Bild noch in Arbeit, dieses verwerfen (Echtzeit vor Vollständigkeit).
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var video = frame?.VideoMediaFrame;
            if (video is null) return;

            SoftwareBitmap? src = video.SoftwareBitmap;
            bool ownsSrc = false;
            if (src is null && video.Direct3DSurface is not null)
            {
                src = await SoftwareBitmap.CreateCopyFromSurfaceAsync(video.Direct3DSurface, BitmapAlphaMode.Premultiplied);
                ownsSrc = true;
            }
            if (src is null) return;

            // Eigene Kopie in BGRA8/premultiplied (Format, das SoftwareBitmapSource verlangt).
            var bmp = SoftwareBitmap.Convert(src, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            if (ownsSrc) src.Dispose();

            var table = _table;
            if (table is not null) ApplyLut(bmp, table);
            FrameReady?.Invoke(this, bmp);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log(ex, "LUT-Vorschau");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static unsafe void ApplyLut(SoftwareBitmap bmp, BgraLutTable table)
    {
        using var buffer = bmp.LockBuffer(BitmapBufferAccessMode.ReadWrite);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference).GetBuffer(out byte* data, out uint capacity);
        nint basePtr = (nint)data + plane.StartIndex;
        int stride = plane.Stride, width = plane.Width;
        Parallel.For(0, plane.Height, y =>
        {
            var row = new Span<byte>((void*)(basePtr + (nint)y * stride), width * 4);
            table.ApplyRow(row, width);
        });
    }

    public async ValueTask DisposeAsync()
    {
        _reader.FrameArrived -= OnFrameArrived;
        try { await _reader.StopAsync(); } catch { }
        try { _reader.Dispose(); } catch { }
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
