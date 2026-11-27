namespace MediaRecorder.Core.Editing;

/// <summary>
/// Ein Abschnitt einer Quelle auf einer Spur. Unveränderlich: jede Bearbeitung erzeugt einen neuen Datensatz,
/// dadurch sind Undo/Redo-Schnappschüsse billig und sicher.
/// </summary>
public sealed record TimelineClip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid AssetId { get; init; }
    /// <summary>0 = V1 (Hauptspur, unten), 1 = V2 (Overlay, liegt im Bild über V1).</summary>
    public required int Track { get; init; }
    /// <summary>Position auf der Timeline.</summary>
    public required TimeSpan Start { get; init; }
    /// <summary>Startpunkt innerhalb der Quelle.</summary>
    public required TimeSpan SourceIn { get; init; }
    /// <summary>Endpunkt innerhalb der Quelle (exklusiv).</summary>
    public required TimeSpan SourceOut { get; init; }
    public bool AudioMuted { get; init; }

    public TimeSpan Duration => SourceOut - SourceIn;
    public TimeSpan End => Start + Duration;

    public bool Contains(TimeSpan t) => t >= Start && t < End;
    public bool Overlaps(TimeSpan start, TimeSpan end) => start < End && end > Start;

    /// <summary>Quellzeit, die an Timeline-Zeit <paramref name="t"/> zu sehen ist.</summary>
    public TimeSpan ToSourceTime(TimeSpan t) => SourceIn + (t - Start);
}
