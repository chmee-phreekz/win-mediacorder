namespace MediaRecorder.Core.Editing;

/// <summary>
/// Hüllkurve einer Audiospur: pro Zeitschritt der größte Betrag (0..1) über alle Kanäle.
/// </summary>
public sealed class Waveform
{
    public Waveform(float[] peaks, int peaksPerSecond)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        if (peaksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(peaksPerSecond));
        Peaks = peaks;
        PeaksPerSecond = peaksPerSecond;
    }

    public float[] Peaks { get; }
    public int PeaksPerSecond { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds(Peaks.Length / (double)PeaksPerSecond);

    /// <summary>Größter Pegel im Quellbereich [from, to). Außerhalb der Daten 0.</summary>
    public float MaxIn(TimeSpan from, TimeSpan to)
    {
        int a = Math.Max(0, (int)Math.Floor(from.TotalSeconds * PeaksPerSecond));
        int b = Math.Min(Peaks.Length, Math.Max(a + 1, (int)Math.Ceiling(to.TotalSeconds * PeaksPerSecond)));
        float max = 0;
        for (int i = a; i < b; i++)
            if (Peaks[i] > max) max = Peaks[i];
        return max;
    }

    /// <summary>
    /// Pegel für <paramref name="columns"/> gleich breite Spalten über den Quellbereich [sourceIn, sourceOut).
    /// Grundlage für das Zeichnen im Clip.
    /// </summary>
    public float[] Sample(TimeSpan sourceIn, TimeSpan sourceOut, int columns)
    {
        var result = new float[Math.Max(0, columns)];
        if (columns <= 0 || sourceOut <= sourceIn) return result;
        double step = (sourceOut - sourceIn).TotalSeconds / columns;
        for (int i = 0; i < columns; i++)
        {
            var from = sourceIn + TimeSpan.FromSeconds(step * i);
            result[i] = MaxIn(from, from + TimeSpan.FromSeconds(step));
        }
        return result;
    }

    /// <summary>
    /// Liest eine PCM-WAV-Datei (16 Bit, beliebige Kanalzahl) und berechnet die Hüllkurve.
    /// </summary>
    public static Waveform FromWav(Stream stream, int peaksPerSecond = 100)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Keine WAV-Datei (RIFF fehlt).");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Keine WAV-Datei (WAVE fehlt).");

        int channels = 0, sampleRate = 0, bits = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            uint size = reader.ReadUInt32();
            if (id == "fmt ")
            {
                long start = stream.Position;
                ushort format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = (int)reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format is not (1 or 0xFFFE)) throw new InvalidDataException("Nur PCM-WAV wird unterstützt.");
                stream.Position = start + size;
            }
            else if (id == "data")
            {
                if (bits != 16 || channels <= 0 || sampleRate <= 0)
                    throw new InvalidDataException("Nur 16-Bit-PCM wird unterstützt.");
                // Manche Encoder schreiben 0 oder 0xFFFFFFFF als Größe, wenn sie die Länge nicht kennen.
                long length = size == 0 || size == uint.MaxValue ? stream.Length - stream.Position : Math.Min(size, stream.Length - stream.Position);
                return ReadPeaks(reader, length, channels, sampleRate, peaksPerSecond);
            }
            else
            {
                stream.Position += size + (size & 1);
            }
        }
        throw new InvalidDataException("WAV-Datei enthält keine Audiodaten.");
    }

    private static Waveform ReadPeaks(BinaryReader reader, long byteLength, int channels, int sampleRate, int peaksPerSecond)
    {
        int framesPerPeak = Math.Max(1, sampleRate / peaksPerSecond);
        long frames = byteLength / (2 * channels);
        var peaks = new float[(int)Math.Ceiling(frames / (double)framesPerPeak)];
        var buffer = new byte[framesPerPeak * channels * 2];

        for (int p = 0; p < peaks.Length; p++)
        {
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            int max = 0;
            for (int i = 0; i + 1 < read; i += 2)
            {
                int v = Math.Abs((int)(short)(buffer[i] | (buffer[i + 1] << 8)));
                if (v > max) max = v;
            }
            peaks[p] = Math.Min(1f, max / 32768f);
        }
        return new Waveform(peaks, peaksPerSecond);
    }
}
