using System.IO.Compression;

namespace MediaRecorder.Core.Editing;

/// <summary>
/// Erzeugt ein einfarbiges PNG in exakt vorgegebener Größe. Dient als Grundbild der MediaComposition,
/// damit deren Koordinatensystem immer genau der Ausgabegröße entspricht (siehe CompositionBuilder).
/// </summary>
public static class SolidPng
{
    public static byte[] Create(int width, int height, byte r = 0, byte g = 0, byte b = 0)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width);
        WriteBE(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // Bit pro Kanal
        ihdr[9] = 2;  // RGB
        WriteChunk(ms, "IHDR", ihdr);

        // Jede Zeile: Filterbyte 0 + RGB-Pixel
        var row = new byte[1 + width * 3];
        for (int x = 0; x < width; x++)
        {
            row[1 + x * 3] = r;
            row[2 + x * 3] = g;
            row[3 + x * 3] = b;
        }
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int y = 0; y < height; y++) z.Write(row);
            }
            WriteChunk(ms, "IDAT", raw.ToArray());
        }
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = new byte[4];
        WriteBE(crc, 0, Crc32(typeBytes, data));
        s.Write(crc);
    }

    private static void WriteBE(byte[] buf, int offset, uint v)
    {
        buf[offset] = (byte)(v >> 24);
        buf[offset + 1] = (byte)(v >> 16);
        buf[offset + 2] = (byte)(v >> 8);
        buf[offset + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
