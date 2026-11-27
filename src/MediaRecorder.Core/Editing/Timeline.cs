namespace MediaRecorder.Core.Editing;

/// <summary>
/// Schnittmodell mit zwei Spuren. Spur 0 (V1) ist die Hauptspur, Spur 1 (V2) liegt im Bild darüber.
/// Clips auf einer Spur überlappen sich nie; Lücken sind erlaubt (V1-Lücken werden schwarz/still exportiert).
/// Jede Änderung ist rückgängig machbar. Die Klasse ist nicht threadsicher und wird vom UI-Thread benutzt.
/// </summary>
public sealed class Timeline
{
    public const int TrackCount = 2;
    public const int MainTrack = 0;
    public const int OverlayTrack = 1;
    public static readonly TimeSpan MinClipDuration = TimeSpan.FromMilliseconds(40);
    private const int MaxUndoSteps = 200;

    private readonly Dictionary<Guid, MediaAsset> _assets = new();
    private List<TimelineClip>[] _tracks = NewTracks();
    private readonly LinkedList<List<TimelineClip>[]> _undo = new();
    private readonly Stack<List<TimelineClip>[]> _redo = new();

    /// <summary>Wird nach jeder Änderung der Clips (inkl. Undo/Redo) ausgelöst.</summary>
    public event EventHandler? Changed;

    /// <summary>Zählt jede Änderung hoch; nützlich, um veraltete Vorschauen zu erkennen.</summary>
    public int Version { get; private set; }

    // ---------- Medienpool ----------

    public IReadOnlyCollection<MediaAsset> Assets => _assets.Values;

    public void RegisterAsset(MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        _assets[asset.Id] = asset;
    }

    public MediaAsset GetAsset(Guid id) =>
        _assets.TryGetValue(id, out var a) ? a : throw new KeyNotFoundException($"Unbekanntes Medium {id}.");

    public bool IsAssetInUse(Guid id) => AllClips.Any(c => c.AssetId == id);

    /// <summary>Entfernt ein Medium aus dem Pool; nur möglich, wenn es auf keiner Spur liegt (auch nicht im Undo-Verlauf).</summary>
    public bool TryRemoveAsset(Guid id)
    {
        bool referenced = IsAssetInUse(id)
            || _undo.Any(s => s.Any(t => t.Any(c => c.AssetId == id)))
            || _redo.Any(s => s.Any(t => t.Any(c => c.AssetId == id)));
        return !referenced && _assets.Remove(id);
    }

    // ---------- Abfragen ----------

    public IReadOnlyList<TimelineClip> GetClips(int track)
    {
        ValidateTrack(track);
        return _tracks[track];
    }

    public IEnumerable<TimelineClip> AllClips => _tracks.SelectMany(t => t);

    public TimelineClip? Find(Guid id) => AllClips.FirstOrDefault(c => c.Id == id);

    public TimelineClip? ClipAt(int track, TimeSpan t) => GetClips(track).FirstOrDefault(c => c.Contains(t));

    public TimeSpan Duration => _tracks.SelectMany(t => t).Select(c => c.End).DefaultIfEmpty(TimeSpan.Zero).Max();

    public bool IsEmpty => _tracks.All(t => t.Count == 0);

    public bool CanPlace(int track, TimeSpan start, TimeSpan duration, Guid? ignoreClipId = null)
    {
        ValidateTrack(track);
        if (start < TimeSpan.Zero) return false;
        var end = start + duration;
        return !_tracks[track].Any(c => c.Id != ignoreClipId && c.Overlaps(start, end));
    }

    /// <summary>Frühester Start ab <paramref name="desired"/>, an dem ein Clip dieser Länge ohne Überlappung passt.</summary>
    public TimeSpan FindFreeStart(int track, TimeSpan desired, TimeSpan duration, Guid? ignoreClipId = null)
    {
        ValidateTrack(track);
        var t = desired < TimeSpan.Zero ? TimeSpan.Zero : desired;
        foreach (var c in _tracks[track]) // sortiert und überlappungsfrei
        {
            if (c.Id == ignoreClipId || c.End <= t) continue;
            if (c.Start >= t + duration) break;
            t = c.End;
        }
        return t;
    }

    /// <summary>
    /// Rastet <paramref name="t"/> auf die nächste Clipkante (oder 0 bzw. zusätzliche Punkte wie den Abspielkopf) ein,
    /// wenn sie näher als <paramref name="threshold"/> liegt.
    /// </summary>
    public TimeSpan Snap(TimeSpan t, TimeSpan threshold, Guid? ignoreClipId = null, params TimeSpan[] extraPoints)
    {
        var candidates = AllClips.Where(c => c.Id != ignoreClipId)
            .SelectMany(c => new[] { c.Start, c.End })
            .Append(TimeSpan.Zero)
            .Concat(extraPoints);
        var best = t;
        var bestDist = threshold;
        foreach (var p in candidates)
        {
            var d = (p - t).Duration();
            if (d <= bestDist) { best = p; bestDist = d; }
        }
        return best;
    }

    // ---------- Bearbeitung ----------

    /// <summary>
    /// Legt ein Medium auf eine Spur. Ohne Startzeit wird ans Spurende angehängt; ist die gewünschte Stelle belegt,
    /// rückt der Clip auf die nächste freie Stelle.
    /// </summary>
    public TimelineClip AddClip(Guid assetId, int track, TimeSpan? start = null)
    {
        ValidateTrack(track);
        var asset = GetAsset(assetId);
        var duration = asset.Kind == MediaKind.Image ? MediaAsset.DefaultImageDuration : asset.Duration;
        if (duration < MinClipDuration)
            throw new InvalidOperationException($"„{asset.DisplayName}“ ist zu kurz für die Timeline.");

        var desired = start ?? TrackEnd(track);
        var clip = new TimelineClip
        {
            AssetId = assetId,
            Track = track,
            Start = FindFreeStart(track, desired, duration),
            SourceIn = TimeSpan.Zero,
            SourceOut = duration,
        };
        Mutate(() => Insert(clip));
        return clip;
    }

    /// <summary>Verschiebt einen Clip (auch auf die andere Spur). Liefert false, wenn das Ziel belegt ist.</summary>
    public bool MoveClip(Guid clipId, int track, TimeSpan start)
    {
        ValidateTrack(track);
        var clip = Require(clipId);
        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
        if (clip.Track == track && clip.Start == start) return false;
        if (!CanPlace(track, start, clip.Duration, clip.Id)) return false;

        Mutate(() =>
        {
            RemoveInternal(clip.Id);
            Insert(clip with { Track = track, Start = start });
        });
        return true;
    }

    /// <summary>
    /// Teilt an Timeline-Zeit <paramref name="at"/>. Mit <paramref name="clipId"/> nur diesen Clip,
    /// sonst alle Clips auf beiden Spuren, die an dieser Stelle liegen. Liefert die Ids der neuen rechten Teile.
    /// </summary>
    public IReadOnlyList<Guid> Split(TimeSpan at, Guid? clipId = null)
    {
        var targets = (clipId is { } id ? new[] { Require(id) } : AllClips.ToArray())
            .Where(c => at - c.Start >= MinClipDuration && c.End - at >= MinClipDuration)
            .ToList();
        if (targets.Count == 0) return [];

        var created = new List<Guid>();
        Mutate(() =>
        {
            foreach (var c in targets)
            {
                var cut = c.ToSourceTime(at);
                var right = c with { Id = Guid.NewGuid(), Start = at, SourceIn = cut };
                Replace(c with { SourceOut = cut });
                Insert(right);
                created.Add(right.Id);
            }
        });
        return created;
    }

    /// <summary>Entfernt einen Clip. Mit <paramref name="ripple"/> rücken die folgenden Clips derselben Spur nach.</summary>
    public bool RemoveClip(Guid clipId, bool ripple = false)
    {
        var clip = Find(clipId);
        if (clip is null) return false;
        Mutate(() =>
        {
            RemoveInternal(clip.Id);
            if (ripple)
                ShiftAfter(clip.Track, clip.End, -clip.Duration);
        });
        return true;
    }

    /// <summary>
    /// Schneidet den Bereich [start, end) auf beiden Spuren heraus. Clips, die die Grenzen überragen, werden gekürzt
    /// bzw. geteilt. Mit <paramref name="ripple"/> (Standard) schließt sich die Lücke, beide Spuren bleiben synchron.
    /// </summary>
    public bool CutRange(TimeSpan start, TimeSpan end, bool ripple = true)
    {
        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
        if (end - start < MinClipDuration) return false;
        if (!AllClips.Any(c => c.Overlaps(start, end) || (ripple && c.Start >= end))) return false;

        var length = end - start;
        Mutate(() =>
        {
            for (int t = 0; t < TrackCount; t++)
            {
                var result = new List<TimelineClip>();
                foreach (var c in _tracks[t])
                {
                    if (c.End <= start)
                    {
                        result.Add(c);
                    }
                    else if (c.Start >= end)
                    {
                        result.Add(ripple ? c with { Start = c.Start - length } : c);
                    }
                    else
                    {
                        if (c.Start < start)
                            result.Add(c with { SourceOut = c.ToSourceTime(start) });
                        if (c.End > end)
                        {
                            bool keepId = c.Start >= start; // linker Teil fällt weg -> Id bleibt am rechten Teil
                            result.Add(c with
                            {
                                Id = keepId ? c.Id : Guid.NewGuid(),
                                Start = ripple ? start : end,
                                SourceIn = c.ToSourceTime(end),
                            });
                        }
                    }
                }
                _tracks[t] = result.Where(c => c.Duration >= MinClipDuration).OrderBy(c => c.Start).ToList();
            }
        });
        return true;
    }

    /// <summary>Verschiebt die linke Kante eines Clips (Ende bleibt stehen). Wird an Quelle und Nachbarn begrenzt.</summary>
    public bool TrimStart(Guid clipId, TimeSpan newStart)
    {
        var c = Require(clipId);
        var prevEnd = _tracks[c.Track].Where(o => o.Id != c.Id && o.End <= c.Start).Select(o => o.End)
            .DefaultIfEmpty(TimeSpan.Zero).Max();
        var min = Max(prevEnd, c.Start - c.SourceIn); // nicht vor Quellanfang
        var max = c.End - MinClipDuration;
        newStart = Clamp(newStart, min, max);
        if (newStart == c.Start) return false;

        var delta = newStart - c.Start;
        Mutate(() => Replace(c with { Start = newStart, SourceIn = c.SourceIn + delta }));
        return true;
    }

    /// <summary>Verschiebt die rechte Kante eines Clips. Wird an Quelllänge und Nachbarn begrenzt.</summary>
    public bool TrimEnd(Guid clipId, TimeSpan newEnd)
    {
        var c = Require(clipId);
        var asset = GetAsset(c.AssetId);
        var nextStart = _tracks[c.Track].Where(o => o.Id != c.Id && o.Start >= c.End).Select(o => o.Start)
            .DefaultIfEmpty(TimeSpan.MaxValue).Min();
        var max = Min(nextStart, c.Start + (asset.MaxSourceDuration - c.SourceIn));
        var min = c.Start + MinClipDuration;
        newEnd = Clamp(newEnd, min, max);
        if (newEnd == c.End) return false;

        Mutate(() => Replace(c with { SourceOut = c.SourceIn + (newEnd - c.Start) }));
        return true;
    }

    public bool SetAudioMuted(Guid clipId, bool muted)
    {
        var c = Require(clipId);
        if (c.AudioMuted == muted) return false;
        Mutate(() => Replace(c with { AudioMuted = muted }));
        return true;
    }

    public void Clear()
    {
        if (IsEmpty) return;
        Mutate(() => _tracks = NewTracks());
    }

    // ---------- Undo/Redo ----------

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public bool Undo()
    {
        if (_undo.Last is not { } last) return false;
        _undo.RemoveLast();
        _redo.Push(Snapshot());
        _tracks = last.Value;
        RaiseChanged();
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out var next)) return false;
        PushUndo(Snapshot());
        _tracks = next;
        RaiseChanged();
        return true;
    }

    // ---------- intern ----------

    private void Mutate(Action change)
    {
        var before = Snapshot();
        try
        {
            change();
        }
        catch
        {
            _tracks = before; // Modell bleibt bei Fehlern konsistent
            throw;
        }
        PushUndo(before);
        _redo.Clear();
        RaiseChanged();
    }

    private void PushUndo(List<TimelineClip>[] state)
    {
        _undo.AddLast(state);
        if (_undo.Count > MaxUndoSteps) _undo.RemoveFirst();
    }

    private List<TimelineClip>[] Snapshot() => _tracks.Select(t => new List<TimelineClip>(t)).ToArray();

    private void RaiseChanged()
    {
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private TimeSpan TrackEnd(int track) => _tracks[track].Select(c => c.End).DefaultIfEmpty(TimeSpan.Zero).Max();

    private void Insert(TimelineClip clip)
    {
        if (!CanPlace(clip.Track, clip.Start, clip.Duration, clip.Id))
            throw new InvalidOperationException("Clip würde einen anderen Clip überlappen.");
        var list = _tracks[clip.Track];
        int i = list.FindIndex(c => c.Start > clip.Start);
        list.Insert(i < 0 ? list.Count : i, clip);
    }

    private void Replace(TimelineClip clip)
    {
        var list = _tracks[clip.Track];
        int i = list.FindIndex(c => c.Id == clip.Id);
        if (i < 0) throw new KeyNotFoundException();
        list[i] = clip;
    }

    private void RemoveInternal(Guid id)
    {
        foreach (var t in _tracks) t.RemoveAll(c => c.Id == id);
    }

    private void ShiftAfter(int track, TimeSpan from, TimeSpan delta)
    {
        var list = _tracks[track];
        for (int i = 0; i < list.Count; i++)
            if (list[i].Start >= from)
                list[i] = list[i] with { Start = list[i].Start + delta };
    }

    private TimelineClip Require(Guid id) => Find(id) ?? throw new KeyNotFoundException($"Unbekannter Clip {id}.");

    private static void ValidateTrack(int track)
    {
        if (track is < 0 or >= TrackCount) throw new ArgumentOutOfRangeException(nameof(track));
    }

    private static List<TimelineClip>[] NewTracks() => Enumerable.Range(0, TrackCount).Select(_ => new List<TimelineClip>()).ToArray();
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;
}
