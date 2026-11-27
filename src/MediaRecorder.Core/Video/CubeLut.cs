using System.Globalization;

namespace MediaRecorder.Core.Video;

/// <summary>
/// 3D-LUT im Adobe/Resolve-.cube-Format (LUT_3D_SIZE, optional LUT_1D_SIZE, DOMAIN_MIN/MAX).
/// Werte im Bereich 0…1, Abtastung trilinear.
/// </summary>
public sealed class CubeLut
{
    /// <summary>Kantenlänge des Gitters (z.B. 33 oder 65).</summary>
    public int Size { get; }
    public string Title { get; }

    // Index = r + g*Size + b*Size*Size (Rot läuft am schnellsten, wie in der Datei).
    private readonly float[] _r, _g, _b;
    private readonly float _domMinR, _domMinG, _domMinB, _domScaleR, _domScaleG, _domScaleB;

    private CubeLut(int size, string title, float[] r, float[] g, float[] b, float[] domMin, float[] domMax)
    {
        Size = size; Title = title; _r = r; _g = g; _b = b;
        _domMinR = domMin[0]; _domMinG = domMin[1]; _domMinB = domMin[2];
        _domScaleR = 1f / Math.Max(1e-6f, domMax[0] - domMin[0]);
        _domScaleG = 1f / Math.Max(1e-6f, domMax[1] - domMin[1]);
        _domScaleB = 1f / Math.Max(1e-6f, domMax[2] - domMin[2]);
    }

    public static CubeLut Load(string path) => Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    public static CubeLut Parse(string text, string fallbackTitle = "LUT")
    {
        int size3 = 0, size1 = 0;
        string title = fallbackTitle;
        float[] domMin = [0, 0, 0], domMax = [1, 1, 1];
        var values = new List<float>(35_937 * 3);
        int lineNo = 0;

        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;

            if (char.IsLetter(line[0]))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0].ToUpperInvariant())
                {
                    case "TITLE":
                        title = line[5..].Trim().Trim('"');
                        break;
                    case "LUT_3D_SIZE":
                        size3 = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        break;
                    case "LUT_1D_SIZE":
                        size1 = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        break;
                    case "DOMAIN_MIN":
                        domMin = ParseTriple(parts, lineNo);
                        break;
                    case "DOMAIN_MAX":
                        domMax = ParseTriple(parts, lineNo);
                        break;
                    // LUT_3D_INPUT_RANGE u.ä. werden ignoriert.
                }
                continue;
            }

            var nums = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length < 3) throw new FormatException($"Zeile {lineNo}: drei Werte erwartet.");
            for (int i = 0; i < 3; i++)
            {
                if (!float.TryParse(nums[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    throw new FormatException($"Zeile {lineNo}: ungültige Zahl „{nums[i]}“.");
                values.Add(v);
            }
        }

        if (size3 > 0)
        {
            if (size3 < 2 || size3 > 256) throw new FormatException($"LUT_3D_SIZE {size3} wird nicht unterstützt (2…256).");
            int n = size3 * size3 * size3;
            if (values.Count != n * 3) throw new FormatException($"Erwartet {n} Einträge, gefunden {values.Count / 3}.");
            var r = new float[n]; var g = new float[n]; var b = new float[n];
            for (int i = 0; i < n; i++) { r[i] = values[3 * i]; g[i] = values[3 * i + 1]; b[i] = values[3 * i + 2]; }
            return new CubeLut(size3, title, r, g, b, domMin, domMax);
        }

        if (size1 > 0)
        {
            // 1D-LUT pro Kanal in ein 33er-3D-Gitter überführen, damit nur ein Anwendungsweg nötig ist.
            if (values.Count != size1 * 3) throw new FormatException($"Erwartet {size1} Einträge, gefunden {values.Count / 3}.");
            const int s = 33;
            float Lookup(int channel, float x)
            {
                float pos = Math.Clamp(x, 0, 1) * (size1 - 1);
                int i0 = (int)pos; int i1 = Math.Min(i0 + 1, size1 - 1); float t = pos - i0;
                return values[3 * i0 + channel] * (1 - t) + values[3 * i1 + channel] * t;
            }
            int n = s * s * s;
            var r = new float[n]; var g = new float[n]; var b = new float[n];
            for (int bi = 0; bi < s; bi++)
            for (int gi = 0; gi < s; gi++)
            for (int ri = 0; ri < s; ri++)
            {
                int i = ri + gi * s + bi * s * s;
                r[i] = Lookup(0, ri / (s - 1f)); g[i] = Lookup(1, gi / (s - 1f)); b[i] = Lookup(2, bi / (s - 1f));
            }
            return new CubeLut(s, title, r, g, b, domMin, domMax);
        }

        throw new FormatException("Keine LUT_3D_SIZE- oder LUT_1D_SIZE-Angabe gefunden.");
    }

    private static float[] ParseTriple(string[] parts, int lineNo)
    {
        if (parts.Length < 4) throw new FormatException($"Zeile {lineNo}: drei Werte erwartet.");
        return [float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture), float.Parse(parts[3], CultureInfo.InvariantCulture)];
    }

    /// <summary>Trilineare Abtastung; Ein- und Ausgabe 0…1.</summary>
    public (float R, float G, float B) Sample(float r, float g, float b)
    {
        int s = Size, m = s - 1;
        float fr = Math.Clamp((r - _domMinR) * _domScaleR, 0, 1) * m;
        float fg = Math.Clamp((g - _domMinG) * _domScaleG, 0, 1) * m;
        float fb = Math.Clamp((b - _domMinB) * _domScaleB, 0, 1) * m;
        int r0 = Math.Min((int)fr, m - 1), g0 = Math.Min((int)fg, m - 1), b0 = Math.Min((int)fb, m - 1);
        float tr = fr - r0, tg = fg - g0, tb = fb - b0;
        int s2 = s * s;
        int i000 = r0 + g0 * s + b0 * s2;
        int i100 = i000 + 1, i010 = i000 + s, i110 = i010 + 1;
        int i001 = i000 + s2, i101 = i001 + 1, i011 = i001 + s, i111 = i011 + 1;

        float Tri(float[] c) =>
            Lerp(Lerp(Lerp(c[i000], c[i100], tr), Lerp(c[i010], c[i110], tr), tg),
                 Lerp(Lerp(c[i001], c[i101], tr), Lerp(c[i011], c[i111], tr), tg), tb);

        return (Tri(_r), Tri(_g), Tri(_b));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>LUT-Auswahl der Aufnahmeseite (wird mit den Aufnahme-Einstellungen gespeichert).</summary>
public sealed record LutSettings
{
    public bool Enabled { get; init; }
    /// <summary>Vollständiger Pfad zur .cube-Datei.</summary>
    public string? Path { get; init; }
}
