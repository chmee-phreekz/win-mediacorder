using MediaRecorder.Core.Video;

namespace MediaRecorder.Core.Tests;

public class LutTests
{
    private static string IdentityCube(int n)
    {
        var sb = new System.Text.StringBuilder($"TITLE \"Identity\"\nLUT_3D_SIZE {n}\n");
        for (int b = 0; b < n; b++)
        for (int g = 0; g < n; g++)
        for (int r = 0; r < n; r++)
            sb.Append(FormattableString.Invariant($"{r / (n - 1.0):0.######} {g / (n - 1.0):0.######} {b / (n - 1.0):0.######}\n"));
        return sb.ToString();
    }

    [Fact]
    public void Parse_Identity_SamplesBackInput()
    {
        var lut = CubeLut.Parse(IdentityCube(17));
        Assert.Equal(17, lut.Size);
        Assert.Equal("Identity", lut.Title);
        var (r, g, b) = lut.Sample(0.3f, 0.6f, 0.9f);
        Assert.Equal(0.3f, r, 3); Assert.Equal(0.6f, g, 3); Assert.Equal(0.9f, b, 3);
    }

    [Fact]
    public void Parse_Invert_Works()
    {
        var text = "LUT_3D_SIZE 2\n1 1 1\n0 1 1\n1 0 1\n0 0 1\n1 1 0\n0 1 0\n1 0 0\n0 0 0\n";
        var lut = CubeLut.Parse(text);
        var (r, g, b) = lut.Sample(0.25f, 0.5f, 1f);
        Assert.Equal(0.75f, r, 3); Assert.Equal(0.5f, g, 3); Assert.Equal(0f, b, 3);
    }

    [Fact]
    public void Parse_RejectsWrongCount()
    {
        Assert.Throws<FormatException>(() => CubeLut.Parse("LUT_3D_SIZE 2\n0 0 0\n"));
    }

    [Fact]
    public void Parse_1D_IsConverted()
    {
        var lut = CubeLut.Parse("LUT_1D_SIZE 2\n1 1 1\n0 0 0\n"); // invertiert
        var (r, _, _) = lut.Sample(0.2f, 0, 0);
        Assert.Equal(0.8f, r, 2);
    }

    [Fact]
    public void Nv12_Identity_KeepsPixelsClose()
    {
        var tables = new Nv12LutTables(CubeLut.Parse(IdentityCube(33)), YuvMatrix.Bt709);
        const int w = 8, h = 4;
        var y = new byte[w * h]; var uv = new byte[w * h / 2];
        var rnd = new Random(1);
        for (int i = 0; i < y.Length; i++) y[i] = (byte)rnd.Next(60, 200);
        for (int i = 0; i < uv.Length; i++) uv[i] = (byte)rnd.Next(118, 138);
        var oy = new byte[y.Length]; var ouv = new byte[uv.Length];
        tables.Apply(y, w, uv, w, oy, w, ouv, w, w, h);
        for (int i = 0; i < y.Length; i++) Assert.InRange(oy[i] - y[i], -3, 3);
        for (int i = 0; i < uv.Length; i++) Assert.InRange(ouv[i] - uv[i], -3, 3);
    }

    [Fact]
    public void Bgra_Identity_KeepsPixels()
    {
        var table = new BgraLutTable(CubeLut.Parse(IdentityCube(33)));
        var row = new byte[] { 10, 128, 250, 0, 0, 0, 0, 0, 255, 255, 255, 0 };
        var copy = (byte[])row.Clone();
        table.ApplyRow(row, 3);
        for (int px = 0; px < 3; px++)
        {
            for (int c = 0; c < 3; c++) Assert.InRange(row[px * 4 + c] - copy[px * 4 + c], -1, 1);
            Assert.Equal(255, row[px * 4 + 3]);
        }
    }
}
