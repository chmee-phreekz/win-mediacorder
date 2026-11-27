namespace MediaRecorder.Core.Video;

/// <summary>YCbCr-Farbraum eines NV12-Videos (begrenzter Bereich 16–235/240).</summary>
public enum YuvMatrix
{
    Bt601,
    Bt709,
}

/// <summary>
/// Vorberechnete Tabellen, um eine <see cref="CubeLut"/> in Echtzeit auf NV12-Videobilder anzuwenden
/// (Aufnahmepfad). Statt pro Pixel YUV→RGB→LUT→YUV zu rechnen, gibt es zwei Nachschlagetabellen:
/// Luma (Y 8 Bit × U,V je 6 Bit) und Chroma (Y 6 Bit × U,V je 7 Bit). Bauzeit ca. 0,1 s, Anwendung
/// wenige Millisekunden pro 1080p-Bild.
/// </summary>
public sealed class Nv12LutTables
{
    private readonly byte[] _yTable = new byte[256 * 64 * 64];
    private readonly byte[] _uvTable = new byte[64 * 128 * 128 * 2];

    public YuvMatrix Matrix { get; }

    public Nv12LutTables(CubeLut lut, YuvMatrix matrix)
    {
        Matrix = matrix;
        var (kr, kb) = matrix == YuvMatrix.Bt709 ? (0.2126f, 0.0722f) : (0.299f, 0.114f);

        Parallel.For(0, 256, y =>
        {
            for (int u = 0; u < 64; u++)
            for (int v = 0; v < 64; v++)
            {
                var (ny, _, _) = Map(lut, kr, kb, y, u * 4 + 2, v * 4 + 2);
                _yTable[(y << 12) | (u << 6) | v] = ny;
            }
        });
        Parallel.For(0, 64, y =>
        {
            for (int u = 0; u < 128; u++)
            for (int v = 0; v < 128; v++)
            {
                var (_, nu, nv) = Map(lut, kr, kb, y * 4 + 2, u * 2 + 1, v * 2 + 1);
                int i = ((y << 14) | (u << 7) | v) * 2;
                _uvTable[i] = nu;
                _uvTable[i + 1] = nv;
            }
        });
    }

    private static (byte Y, byte U, byte V) Map(CubeLut lut, float kr, float kb, int Y, int U, int V)
    {
        float kg = 1 - kr - kb;
        float yy = (Y - 16) / 219f, pb = (U - 128) / 224f, pr = (V - 128) / 224f;
        float r = yy + 2 * (1 - kr) * pr;
        float b = yy + 2 * (1 - kb) * pb;
        float g = (yy - kr * r - kb * b) / kg;

        var (lr, lg, lb) = lut.Sample(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));

        float ny = kr * lr + kg * lg + kb * lb;
        float npb = (lb - ny) / (2 * (1 - kb));
        float npr = (lr - ny) / (2 * (1 - kr));
        return (ToByte(16 + 219 * ny), ToByte(128 + 224 * npb), ToByte(128 + 224 * npr));
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    /// <summary>
    /// Wendet die LUT auf ein NV12-Bild an (Quelle und Ziel dürfen identisch sein).
    /// Ebenen: Y (height Zeilen), verschachteltes UV (height/2 Zeilen).
    /// </summary>
    public void Apply(ReadOnlySpan<byte> srcY, int srcYStride, ReadOnlySpan<byte> srcUv, int srcUvStride,
                      Span<byte> dstY, int dstYStride, Span<byte> dstUv, int dstUvStride, int width, int height)
    {
        int pairs = height / 2;
        int w2 = width & ~1;
        for (int p = 0; p < pairs; p++)
            ApplyRowPair(srcY, srcYStride, srcUv, srcUvStride, dstY, dstYStride, dstUv, dstUvStride, w2, p);
    }

    /// <summary>Zwei Luma-Zeilen und eine Chroma-Zeile; unabhängig voneinander, daher parallelisierbar.</summary>
    public void ApplyRowPair(ReadOnlySpan<byte> srcY, int srcYStride, ReadOnlySpan<byte> srcUv, int srcUvStride,
                             Span<byte> dstY, int dstYStride, Span<byte> dstUv, int dstUvStride, int width, int pair)
    {
        var yt = _yTable;
        var uvt = _uvTable;
        int sy0 = 2 * pair * srcYStride, sy1 = sy0 + srcYStride;
        int dy0 = 2 * pair * dstYStride, dy1 = dy0 + dstYStride;
        int suv = pair * srcUvStride, duv = pair * dstUvStride;

        for (int x = 0; x < width; x += 2)
        {
            int u = srcUv[suv + x], v = srcUv[suv + x + 1];
            int a = srcY[sy0 + x], b = srcY[sy0 + x + 1], c = srcY[sy1 + x], d = srcY[sy1 + x + 1];
            int cu = (u >> 2) << 6 | (v >> 2);
            dstY[dy0 + x] = yt[(a << 12) | cu];
            dstY[dy0 + x + 1] = yt[(b << 12) | cu];
            dstY[dy1 + x] = yt[(c << 12) | cu];
            dstY[dy1 + x + 1] = yt[(d << 12) | cu];

            int avg = (a + b + c + d + 2) >> 2;
            int i = (((avg >> 2) << 14) | ((u >> 1) << 7) | (v >> 1)) * 2;
            dstUv[duv + x] = uvt[i];
            dstUv[duv + x + 1] = uvt[i + 1];
        }
    }
}

/// <summary>
/// Dichte RGB-Tabelle (65³) für die Vorschau mit BGRA8-Bildern, trilinear in Ganzzahlarithmetik.
/// </summary>
public sealed class BgraLutTable
{
    private const int N = 65; // Gitterpunkte bei Schrittweite 4 (0 … 256)
    private readonly byte[] _table = new byte[N * N * N * 3];

    public BgraLutTable(CubeLut lut)
    {
        Parallel.For(0, N, b =>
        {
            for (int g = 0; g < N; g++)
            for (int r = 0; r < N; r++)
            {
                var (lr, lg, lb) = lut.Sample(Math.Min(r * 4, 255) / 255f, Math.Min(g * 4, 255) / 255f, Math.Min(b * 4, 255) / 255f);
                int i = (r + g * N + b * N * N) * 3;
                _table[i] = ToByte(lr); _table[i + 1] = ToByte(lg); _table[i + 2] = ToByte(lb);
            }
        });
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);

    /// <summary>Eine Zeile BGRA8 an Ort und Stelle; Alpha wird auf 255 gesetzt.</summary>
    public void ApplyRow(Span<byte> bgra, int width)
    {
        var t = _table;
        for (int x = 0; x < width; x++)
        {
            int o = x * 4;
            int B = bgra[o], G = bgra[o + 1], R = bgra[o + 2];
            int r0 = R >> 2, g0 = G >> 2, b0 = B >> 2;
            int fr = R & 3, fg = G & 3, fb = B & 3; // Gewichte in Vierteln
            int i000 = (r0 + g0 * N + b0 * N * N) * 3;
            const int dr = 3, dg = N * 3, db = N * N * 3;
            for (int c = 0; c < 3; c++)
            {
                int c000 = t[i000 + c], c100 = t[i000 + dr + c], c010 = t[i000 + dg + c], c110 = t[i000 + dr + dg + c];
                int c001 = t[i000 + db + c], c101 = t[i000 + dr + db + c], c011 = t[i000 + dg + db + c], c111 = t[i000 + dr + dg + db + c];
                int c00 = c000 * (4 - fr) + c100 * fr, c10 = c010 * (4 - fr) + c110 * fr;
                int c01 = c001 * (4 - fr) + c101 * fr, c11 = c011 * (4 - fr) + c111 * fr;
                int c0 = c00 * (4 - fg) + c10 * fg, c1 = c01 * (4 - fg) + c11 * fg;
                int val = (c0 * (4 - fb) + c1 * fb + 32) >> 6;
                bgra[o + 2 - c] = (byte)val; // c=0 -> R (Offset 2), c=1 -> G (1), c=2 -> B (0)
            }
            bgra[o + 3] = 255;
        }
    }
}
