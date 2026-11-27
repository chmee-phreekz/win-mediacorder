using MediaRecorder.Core.Editing;

namespace MediaRecorder.Core.Tests;

public class TimelineTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    private static (Timeline tl, MediaAsset a, MediaAsset b) Setup()
    {
        var tl = new Timeline();
        var a = new MediaAsset { Path = @"C:\a.mp4", Kind = MediaKind.Video, Duration = S(10), Width = 1920, Height = 1080, HasAudio = true };
        var b = new MediaAsset { Path = @"C:\b.mp4", Kind = MediaKind.Video, Duration = S(4), Width = 1080, Height = 1920 };
        tl.RegisterAsset(a);
        tl.RegisterAsset(b);
        return (tl, a, b);
    }

    [Fact]
    public void AddClip_AppendsAtTrackEnd()
    {
        var (tl, a, b) = Setup();
        var c1 = tl.AddClip(a.Id, 0);
        var c2 = tl.AddClip(b.Id, 0);
        Assert.Equal(S(0), c1.Start);
        Assert.Equal(S(10), c2.Start);
        Assert.Equal(S(14), tl.Duration);
    }

    [Fact]
    public void AddClip_AtOccupiedPosition_MovesToNextFreeSlot()
    {
        var (tl, a, b) = Setup();
        tl.AddClip(a.Id, 0);
        var c = tl.AddClip(b.Id, 0, S(3));
        Assert.Equal(S(10), c.Start);
    }

    [Fact]
    public void AddClip_FitsIntoGap()
    {
        var (tl, a, b) = Setup();
        var first = tl.AddClip(b.Id, 0);             // 0-4
        tl.AddClip(a.Id, 0, S(10));                  // 10-20
        var gapClip = tl.AddClip(b.Id, 0, S(2));     // belegt -> ab 4, passt in Lücke 4-8
        Assert.Equal(first.End, gapClip.Start);
    }

    [Fact]
    public void Split_SelectedClip_KeepsSourceContinuity()
    {
        var (tl, a, _) = Setup();
        var c = tl.AddClip(a.Id, 0);
        var ids = tl.Split(S(4), c.Id);

        Assert.Single(ids);
        var clips = tl.GetClips(0);
        Assert.Equal(2, clips.Count);
        Assert.Equal(S(0), clips[0].SourceIn);
        Assert.Equal(S(4), clips[0].SourceOut);
        Assert.Equal(S(4), clips[1].Start);
        Assert.Equal(S(4), clips[1].SourceIn);
        Assert.Equal(S(10), clips[1].SourceOut);
        Assert.Equal(S(10), tl.Duration);
    }

    [Fact]
    public void Split_WithoutSelection_SplitsBothTracks()
    {
        var (tl, a, b) = Setup();
        tl.AddClip(a.Id, 0);
        tl.AddClip(b.Id, 1, S(1)); // 1-5
        var ids = tl.Split(S(2));
        Assert.Equal(2, ids.Count);
        Assert.Equal(2, tl.GetClips(0).Count);
        Assert.Equal(2, tl.GetClips(1).Count);
    }

    [Fact]
    public void Split_AtEdge_DoesNothing()
    {
        var (tl, a, _) = Setup();
        var c = tl.AddClip(a.Id, 0);
        Assert.Empty(tl.Split(S(0), c.Id));
        Assert.Empty(tl.Split(S(10), c.Id));
        Assert.False(tl.CanUndo && tl.GetClips(0).Count != 1);
    }

    [Fact]
    public void CutRange_Ripple_RemovesSectionOnBothTracksAndClosesGap()
    {
        var (tl, a, b) = Setup();
        tl.AddClip(a.Id, 0);        // V1 0-10
        tl.AddClip(b.Id, 1, S(5));  // V2 5-9

        Assert.True(tl.CutRange(S(3), S(6)));

        var v1 = tl.GetClips(0);
        Assert.Equal(2, v1.Count);
        Assert.Equal((S(0), S(0), S(3)), (v1[0].Start, v1[0].SourceIn, v1[0].SourceOut));
        Assert.Equal((S(3), S(6), S(10)), (v1[1].Start, v1[1].SourceIn, v1[1].SourceOut));

        var v2 = Assert.Single(tl.GetClips(1));
        Assert.Equal(S(3), v2.Start);       // war 5, linker Teil 5-6 weggeschnitten
        Assert.Equal(S(1), v2.SourceIn);
        Assert.Equal(S(7), tl.Duration);
    }

    [Fact]
    public void CutRange_NoRipple_LeavesGap()
    {
        var (tl, a, _) = Setup();
        tl.AddClip(a.Id, 0);
        tl.CutRange(S(3), S(6), ripple: false);
        var v1 = tl.GetClips(0);
        Assert.Equal(S(6), v1[1].Start);
        Assert.Equal(S(10), tl.Duration);
    }

    [Fact]
    public void RemoveClip_Ripple_ShiftsFollowingClipsOnSameTrackOnly()
    {
        var (tl, a, b) = Setup();
        var first = tl.AddClip(b.Id, 0);   // 0-4
        var second = tl.AddClip(a.Id, 0);  // 4-14
        var overlay = tl.AddClip(b.Id, 1, S(6));

        tl.RemoveClip(first.Id, ripple: true);

        Assert.Equal(S(0), tl.Find(second.Id)!.Start);
        Assert.Equal(S(6), tl.Find(overlay.Id)!.Start);
    }

    [Fact]
    public void MoveClip_RejectsOverlap_AllowsOtherTrack()
    {
        var (tl, a, b) = Setup();
        tl.AddClip(a.Id, 0);
        var c = tl.AddClip(b.Id, 0); // 10-14
        Assert.False(tl.MoveClip(c.Id, 0, S(8)));
        Assert.True(tl.MoveClip(c.Id, 1, S(8)));
        Assert.Equal(1, tl.Find(c.Id)!.Track);
    }

    [Fact]
    public void Trim_IsLimitedBySourceAndNeighbours()
    {
        var (tl, a, b) = Setup();
        var c1 = tl.AddClip(b.Id, 0);        // 0-4, Quelle 4 s
        var c2 = tl.AddClip(a.Id, 0, S(6));  // 6-16

        tl.TrimEnd(c1.Id, S(20));            // Quelle nur 4 s
        Assert.Equal(S(4), tl.Find(c1.Id)!.End);

        tl.TrimStart(c2.Id, S(8));
        var t = tl.Find(c2.Id)!;
        Assert.Equal((S(8), S(2)), (t.Start, t.SourceIn));

        tl.TrimStart(c2.Id, S(0));           // darf nicht vor Quellanfang
        t = tl.Find(c2.Id)!;
        Assert.Equal((S(6), S(0)), (t.Start, t.SourceIn));
    }

    [Fact]
    public void ImageClips_CanBeExtended()
    {
        var tl = new Timeline();
        var img = new MediaAsset { Path = "x.png", Kind = MediaKind.Image, Duration = MediaAsset.DefaultImageDuration, Width = 800, Height = 600 };
        tl.RegisterAsset(img);
        var c = tl.AddClip(img.Id, 1);
        Assert.True(tl.TrimEnd(c.Id, S(30)));
        Assert.Equal(S(30), tl.Find(c.Id)!.End);
    }

    [Fact]
    public void UndoRedo_RestoresStates()
    {
        var (tl, a, _) = Setup();
        var c = tl.AddClip(a.Id, 0);
        tl.Split(S(5), c.Id);
        Assert.Equal(2, tl.GetClips(0).Count);

        Assert.True(tl.Undo());
        Assert.Single(tl.GetClips(0));
        Assert.True(tl.Undo());
        Assert.Empty(tl.GetClips(0));
        Assert.False(tl.Undo());

        Assert.True(tl.Redo());
        Assert.True(tl.Redo());
        Assert.Equal(2, tl.GetClips(0).Count);
        Assert.False(tl.CanRedo);
    }

    [Fact]
    public void TryRemoveAsset_FailsWhileReferenced()
    {
        var (tl, a, b) = Setup();
        tl.AddClip(a.Id, 0);
        Assert.False(tl.TryRemoveAsset(a.Id));
        Assert.True(tl.TryRemoveAsset(b.Id));
    }

    [Fact]
    public void Snap_FindsNearestEdge()
    {
        var (tl, a, _) = Setup();
        tl.AddClip(a.Id, 0);
        Assert.Equal(S(10), tl.Snap(S(9.9), S(0.2)));
        Assert.Equal(S(9.5), tl.Snap(S(9.5), S(0.2)));
        Assert.Equal(S(7), tl.Snap(S(7.05), S(0.2), null, S(7)));
    }

    [Fact]
    public void Changed_IsRaisedPerEdit()
    {
        var (tl, a, _) = Setup();
        int n = 0;
        tl.Changed += (_, _) => n++;
        var c = tl.AddClip(a.Id, 0);
        tl.Split(S(5), c.Id);
        tl.Undo();
        Assert.Equal(3, n);
    }
}

public class GeometryTests
{
    [Theory]
    [InlineData(1920, 1080, 1000, 1000, 0, 218.75, 1000, 562.5)]   // 16:9 in Quadrat -> Letterbox
    [InlineData(1080, 1920, 1600, 900, 546.875, 0, 506.25, 900)]   // Hochkant in 16:9 -> Pillarbox
    [InlineData(1280, 720, 1920, 1080, 0, 0, 1920, 1080)]          // gleiches Format -> voll
    public void AspectFit_IsLargestUndistortedCenteredRect(double sw, double sh, double bw, double bh, double x, double y, double w, double h)
    {
        var r = Geometry.AspectFit(sw, sh, bw, bh);
        Assert.Equal(x, r.X, 3);
        Assert.Equal(y, r.Y, 3);
        Assert.Equal(w, r.Width, 3);
        Assert.Equal(h, r.Height, 3);
        Assert.Equal(sw / sh, r.Width / r.Height, 6);
    }

    [Fact]
    public void AspectFitPixels_IsEvenAndInside()
    {
        var r = Geometry.AspectFitPixels(1080, 1920, 1920, 1080);
        Assert.Equal(0, r.Width % 2);
        Assert.Equal(0, r.Height % 2);
        Assert.True(r.X + r.Width <= 1920 && r.Y + r.Height <= 1080);
    }

    [Fact]
    public void Timecode_FormatsFrames()
    {
        Assert.Equal("00:01:02:15", Timecode.Format(TimeSpan.FromSeconds(62.5), 30));
        Assert.Equal("00:00:01.250", Timecode.Format(TimeSpan.FromSeconds(1.25), 0));
    }
}

public class EncodingSettingsTests
{
    [Fact]
    public void AllPresets_AreValid()
    {
        foreach (var p in Encoding.EncodingPresets.All)
            Assert.Empty(p.Settings.Validate());
    }

    [Fact]
    public void Validate_RejectsIncompatibleContainer()
    {
        var s = new Encoding.EncodingSettings { Container = Encoding.ContainerFormat.Wmv };
        Assert.NotEmpty(s.Validate());
    }

    [Fact]
    public void Validate_RejectsOddResolution()
    {
        var s = new Encoding.EncodingSettings { Width = 1921, Height = 1080 };
        Assert.Contains(s.Validate(), e => e.Contains("gerade"));
    }

    [Theory]
    [InlineData(29.97, 30000u, 1001u)]
    [InlineData(59.94, 60000u, 1001u)]
    [InlineData(25, 25u, 1u)]
    public void ToFraction_HandlesNtsc(double fps, uint n, uint d)
    {
        Assert.Equal((n, d), Encoding.EncodingSettings.ToFraction(fps));
    }
}
