namespace MediaRecorder.Core.Editing;

public readonly record struct FitRect(double X, double Y, double Width, double Height);

public static class Geometry
{
    /// <summary>
    /// Größtmögliches Rechteck mit dem Seitenverhältnis der Quelle, zentriert in der Fläche (Letterbox/Pillarbox).
    /// Grundlage dafür, dass importierte Medien nie verzerrt und immer maximal groß dargestellt werden.
    /// </summary>
    public static FitRect AspectFit(double sourceWidth, double sourceHeight, double boxWidth, double boxHeight)
    {
        if (boxWidth <= 0 || boxHeight <= 0)
            return new FitRect(0, 0, 0, 0);
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return new FitRect(0, 0, boxWidth, boxHeight);

        double scale = Math.Min(boxWidth / sourceWidth, boxHeight / sourceHeight);
        double w = sourceWidth * scale, h = sourceHeight * scale;
        return new FitRect((boxWidth - w) / 2, (boxHeight - h) / 2, w, h);
    }

    /// <summary>Wie <see cref="AspectFit"/>, aber auf ganze, gerade Pixel gerundet (für Encoder-Ausgabe).</summary>
    public static FitRect AspectFitPixels(int sourceWidth, int sourceHeight, int boxWidth, int boxHeight)
    {
        var r = AspectFit(sourceWidth, sourceHeight, boxWidth, boxHeight);
        double w = Math.Min(boxWidth, 2 * Math.Round(r.Width / 2));
        double h = Math.Min(boxHeight, 2 * Math.Round(r.Height / 2));
        return new FitRect(Math.Floor((boxWidth - w) / 2), Math.Floor((boxHeight - h) / 2), w, h);
    }
}

public static class Timecode
{
    /// <summary>Formatiert als hh:mm:ss:ff (Frames) bzw. hh:mm:ss.mmm, wenn keine Bildrate bekannt ist.</summary>
    public static string Format(TimeSpan t, double fps = 30)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (fps <= 0)
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
        int nominal = (int)Math.Round(fps);
        long totalFrames = (long)Math.Floor(t.TotalSeconds * fps + 1e-6);
        long frames = totalFrames % nominal;
        var whole = TimeSpan.FromSeconds(Math.Floor(totalFrames / fps + 1e-9));
        return $"{(int)whole.TotalHours:00}:{whole.Minutes:00}:{whole.Seconds:00}:{frames:00}";
    }

    /// <summary>Rastet eine Zeit auf die nächste Bildgrenze ein.</summary>
    public static TimeSpan SnapToFrame(TimeSpan t, double fps)
    {
        if (fps <= 0) return t;
        double frame = Math.Round(t.TotalSeconds * fps);
        return TimeSpan.FromTicks((long)Math.Round(frame / fps * TimeSpan.TicksPerSecond));
    }

    public static TimeSpan FrameDuration(double fps) =>
        fps <= 0 ? TimeSpan.FromMilliseconds(40) : TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond / fps));
}
