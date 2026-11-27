using System;
using System.Collections.Generic;
using System.Linq;
using MediaRecorder.Core.Editing;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace MediaRecorder.App.Controls;

public sealed class AssetDroppedEventArgs(Guid assetId, int track, TimeSpan time) : EventArgs
{
    public Guid AssetId { get; } = assetId;
    public int Track { get; } = track;
    public TimeSpan Time { get; } = time;
}

/// <summary>
/// Zweispurige Timeline (V2 oben, V1 unten) mit Zeitlineal, Abspielkopf, In/Out-Bereich,
/// Verschieben (auch zwischen Spuren), Kanten-Trimmen, Einrasten, Zoom (Strg+Mausrad) und Drag &amp; Drop aus dem Medienpool.
/// Reines Code-Control: zeichnet bei jeder Modelländerung neu; der Abspielkopf wird separat und billig aktualisiert.
/// </summary>
public sealed class TimelineControl : UserControl
{
    /// <summary>Format der Drag-&amp;-Drop-Nutzlast aus dem Medienpool: "asset:{guid}".</summary>
    public const string DragPrefix = "asset:";

    private const double RulerHeight = 30;
    private const double TrackHeight = 78;
    private const double WaveHeight = 30;
    private const double TrackGap = 6;
    private const double EdgeGrab = 7;
    private const double SnapPixels = 8;
    private const double MinPps = 2, MaxPps = 800;
    private static readonly TimeSpan TailPadding = TimeSpan.FromSeconds(30);

    private readonly ScrollViewer _scroll;
    private readonly Canvas _canvas;
    private readonly Rectangle _playheadLine;
    private readonly Polygon _playheadHandle;
    private readonly Dictionary<Guid, Border> _clipVisuals = new();

    private Timeline? _timeline;
    private double _pps = 60;
    private TimeSpan _playhead;
    private Guid? _selected;
    private TimeSpan? _in, _out;

    private enum DragMode { None, Scrub, Move, TrimLeft, TrimRight }
    private DragMode _mode;
    private TimelineClip? _dragClip;
    private TimeSpan _grabOffset;
    private TimeSpan _dragStart;
    private int _dragTrack;
    private TimeSpan _dragEdge;

    public TimelineControl()
    {
        _canvas = new Canvas
        {
            Background = new SolidColorBrush(Colors.Transparent), // nötig für Treffertests auf leeren Flächen
            AllowDrop = true,
            Height = RulerHeight + 2 * TrackHeight + TrackGap + 8,
        };
        _scroll = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ZoomMode = ZoomMode.Disabled,
        };

        _playheadLine = new Rectangle { Width = 2, Fill = Brush("PlayheadColor", Color.FromArgb(255, 255, 77, 79)), IsHitTestVisible = false };
        _playheadHandle = new Polygon
        {
            Points = { new Point(-6, 0), new Point(6, 0), new Point(6, 8), new Point(0, 14), new Point(-6, 8) },
            Fill = _playheadLine.Fill,
            IsHitTestVisible = false,
        };

        var headers = new StackPanel { Width = 96, Spacing = TrackGap, Padding = new Thickness(0, RulerHeight, 0, 0) };
        headers.Children.Add(TrackHeader("V2", "Overlay", "TrackV2Color"));
        headers.Children.Add(TrackHeader("V1", "Hauptspur", "TrackV1Color"));

        var root = new Grid { ColumnSpacing = 4 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_scroll, 1);
        root.Children.Add(headers);
        root.Children.Add(_scroll);
        Content = root;

        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCaptureLost += (_, _) => CancelDrag();
        _canvas.DragOver += OnDragOver;
        _canvas.Drop += OnDrop;
        _scroll.PointerWheelChanged += OnWheel;
        _scroll.SizeChanged += (_, _) => Rebuild();
    }

    // ---------- öffentliche API ----------

    public event EventHandler<TimeSpan>? PlayheadScrubbed;
    public event EventHandler<Guid?>? SelectionChanged;
    public event EventHandler<AssetDroppedEventArgs>? AssetDropped;
    /// <summary>Hinweis für den Nutzer, z. B. wenn eine Zielposition belegt ist.</summary>
    public event EventHandler<string>? EditRejected;

    public Timeline? Timeline
    {
        get => _timeline;
        set
        {
            if (_timeline is not null) _timeline.Changed -= OnTimelineChanged;
            _timeline = value;
            if (_timeline is not null) _timeline.Changed += OnTimelineChanged;
            Rebuild();
        }
    }

    /// <summary>Liefert die Wellenform eines Mediums (null = noch nicht berechnet oder kein Ton).</summary>
    public Func<Guid, Waveform?>? WaveformProvider { get; set; }

    /// <summary>Neu zeichnen, z. B. wenn eine Wellenform fertig berechnet wurde.</summary>
    public void Refresh() => Rebuild();

    /// <summary>Bildrate für Einrasten auf Frames (Projektbildrate).</summary>
    public double FrameRate { get; set; } = 30;

    public double PixelsPerSecond
    {
        get => _pps;
        set { _pps = Math.Clamp(value, MinPps, MaxPps); Rebuild(); }
    }

    public TimeSpan Playhead
    {
        get => _playhead;
        set { _playhead = value < TimeSpan.Zero ? TimeSpan.Zero : value; UpdatePlayhead(); }
    }

    public Guid? SelectedClipId
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Rebuild(); }
    }

    public void SetRange(TimeSpan? inPoint, TimeSpan? outPoint)
    {
        _in = inPoint;
        _out = outPoint;
        Rebuild();
    }

    /// <summary>Passt den Zoom so an, dass die ganze Timeline sichtbar ist.</summary>
    public void ZoomToFit()
    {
        var seconds = Math.Max(5, (_timeline?.Duration ?? TimeSpan.Zero).TotalSeconds * 1.05);
        var width = Math.Max(200, _scroll.ViewportWidth);
        PixelsPerSecond = width / seconds;
        _scroll.ChangeView(0, null, null, true);
    }

    /// <summary>Scrollt mit, wenn der Abspielkopf während der Wiedergabe aus dem sichtbaren Bereich läuft.</summary>
    public void EnsurePlayheadVisible()
    {
        double x = X(_playhead);
        double left = _scroll.HorizontalOffset, right = left + _scroll.ViewportWidth;
        if (x < left || x > right - 40)
            _scroll.ChangeView(Math.Max(0, x - _scroll.ViewportWidth * 0.1), null, null, true);
    }

    // ---------- Zeichnen ----------

    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        if (_selected is { } id && _timeline?.Find(id) is null)
        {
            _selected = null;
            SelectionChanged?.Invoke(this, null);
        }
        Rebuild();
    }

    private void Rebuild()
    {
        _canvas.Children.Clear();
        _clipVisuals.Clear();

        var duration = (_timeline?.Duration ?? TimeSpan.Zero) + TailPadding;
        _canvas.Width = Math.Max(_scroll.ViewportWidth, X(duration));
        double height = _canvas.Height;

        // Spurhintergründe
        for (int track = Timeline.TrackCount - 1; track >= 0; track--)
        {
            var bg = new Rectangle
            {
                Width = _canvas.Width,
                Height = TrackHeight,
                Fill = ThemeBrush("ControlFillColorSecondaryBrush"),
                RadiusX = 4,
                RadiusY = 4,
            };
            Place(bg, 0, TrackY(track));
        }

        DrawRuler(_canvas.Width);
        DrawRange(height);

        if (_timeline is not null)
        {
            foreach (var clip in _timeline.AllClips)
                DrawClip(clip);
        }

        _playheadLine.Height = height;
        _canvas.Children.Add(_playheadLine);
        _canvas.Children.Add(_playheadHandle);
        UpdatePlayhead();
    }

    private void DrawRuler(double width)
    {
        // Abstand der Beschriftungen so wählen, dass sie mindestens ~90 px auseinander liegen.
        double[] steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];
        double step = steps.FirstOrDefault(s => s * _pps >= 90, 3600);
        var tickBrush = ThemeBrush("TextFillColorTertiaryBrush");

        double seconds = width / _pps;
        for (double t = 0; t <= seconds; t += step)
        {
            double x = t * _pps;
            Place(new Rectangle { Width = 1, Height = 10, Fill = tickBrush }, x, RulerHeight - 10);
            Place(new TextBlock
            {
                Text = RulerLabel(TimeSpan.FromSeconds(t), step),
                FontSize = 11,
                Foreground = tickBrush,
                IsHitTestVisible = false,
            }, x + 4, 4);

            for (int i = 1; i < 5; i++)
                Place(new Rectangle { Width = 1, Height = 4, Fill = tickBrush, Opacity = 0.6 }, x + i * step / 5 * _pps, RulerHeight - 4);
        }
    }

    private static string RulerLabel(TimeSpan t, double step) =>
        step < 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}"
                 : t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private void DrawRange(double height)
    {
        if (_in is null && _out is null) return;
        var accent = ThemeColor("SystemAccentColor", Color.FromArgb(255, 0, 120, 215));
        var start = _in ?? TimeSpan.Zero;
        var end = _out ?? (_timeline?.Duration ?? start);
        if (end > start)
        {
            Place(new Rectangle
            {
                Width = X(end) - X(start),
                Height = height,
                Fill = new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B)),
                IsHitTestVisible = false,
            }, X(start), 0);
        }
        foreach (var marker in new[] { _in, _out })
        {
            if (marker is { } m)
                Place(new Rectangle { Width = 2, Height = height, Fill = new SolidColorBrush(accent), IsHitTestVisible = false }, X(m) - 1, 0);
        }
    }

    private void DrawClip(TimelineClip clip)
    {
        var asset = _timeline!.GetAsset(clip.AssetId);
        bool selected = clip.Id == _selected;
        var baseColor = ThemeColor(clip.Track == Timeline.OverlayTrack ? "TrackV2Color" : "TrackV1Color", Color.FromArgb(255, 47, 111, 222));
        if (asset.Kind == MediaKind.Audio)
            baseColor = Color.FromArgb(255, 34, 160, 107); // Audio grün, egal auf welcher Spur

        var label = new StackPanel { Spacing = 2, IsHitTestVisible = false, Margin = new Thickness(8, 5, 8, 0) };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new FontIcon { Glyph = asset.Kind switch { MediaKind.Audio => "", MediaKind.Image => "", _ => "" }, FontSize = 12 });
        if (clip.AudioMuted)
            title.Children.Add(new FontIcon { Glyph = "", FontSize = 12 });
        title.Children.Add(new TextBlock { Text = asset.DisplayName, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        label.Children.Add(title);
        label.Children.Add(new TextBlock { Text = Timecode.Format(clip.Duration, FrameRate), FontSize = 11, Opacity = 0.8 });

        var border = new Border
        {
            Width = Math.Max(2, X(clip.Duration)),
            Height = TrackHeight - 4,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(selected ? Lighten(baseColor, 0.25) : baseColor),
            BorderBrush = new SolidColorBrush(selected ? Colors.White : Lighten(baseColor, 0.35)),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Child = BuildClipContent(clip, asset, label, Math.Max(2, X(clip.Duration))),
            IsHitTestVisible = false, // Treffertests rechnen wir selbst (Kanten-Griffe)
        };
        ToolTipService.SetToolTip(border, $"{asset.DisplayName}\nQuelle {Timecode.Format(clip.SourceIn, FrameRate)} – {Timecode.Format(clip.SourceOut, FrameRate)}");
        Place(border, X(clip.Start), TrackY(clip.Track) + 2);
        _clipVisuals[clip.Id] = border;
    }

    /// <summary>Beschriftung oben, darunter die Wellenform der Tonspur (sofern vorhanden und berechnet).</summary>
    private UIElement BuildClipContent(TimelineClip clip, MediaAsset asset, UIElement label, double width)
    {
        var grid = new Grid { IsHitTestVisible = false };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(WaveHeight) });
        grid.Children.Add(label);

        if (asset.HasAudio && WaveformProvider?.Invoke(asset.Id) is { } wave && width > 4)
        {
            var shape = BuildWaveform(wave, clip, width - 2, WaveHeight - 4);
            shape.Opacity = clip.AudioMuted ? 0.3 : 0.85;
            Grid.SetRow(shape, 1);
            grid.Children.Add(shape);
        }
        else if (asset.HasAudio)
        {
            // Noch nicht berechnet: dezente Mittellinie als Platzhalter für die Tonspur.
            var line = new Rectangle { Height = 1, Fill = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            Grid.SetRow(line, 1);
            grid.Children.Add(line);
        }
        return grid;
    }

    private static Polygon BuildWaveform(Waveform wave, TimelineClip clip, double width, double height)
    {
        int columns = (int)Math.Clamp(width / 2, 1, 3000);
        var levels = wave.Sample(clip.SourceIn, clip.SourceOut, columns);
        double mid = height / 2, step = width / columns;
        var poly = new Polygon
        {
            Fill = new SolidColorBrush(Color.FromArgb(230, 235, 240, 255)),
            Margin = new Thickness(1, 2, 1, 2),
            IsHitTestVisible = false,
        };
        // Obere Hüllkurve von links nach rechts, untere zurück; mindestens 1 px, damit Stille sichtbar bleibt.
        for (int i = 0; i < columns; i++)
        {
            double a = Math.Max(0.5, levels[i] * mid);
            poly.Points.Add(new Point(i * step, mid - a));
            poly.Points.Add(new Point((i + 1) * step, mid - a));
        }
        for (int i = columns - 1; i >= 0; i--)
        {
            double a = Math.Max(0.5, levels[i] * mid);
            poly.Points.Add(new Point((i + 1) * step, mid + a));
            poly.Points.Add(new Point(i * step, mid + a));
        }
        return poly;
    }

    private void UpdatePlayhead()
    {
        double x = X(_playhead);
        Canvas.SetLeft(_playheadLine, x - 1);
        Canvas.SetLeft(_playheadHandle, x);
        Canvas.SetTop(_playheadHandle, 0);
    }

    // ---------- Maus ----------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_timeline is null) return;
        var pt = e.GetCurrentPoint(_canvas);
        if (!pt.Properties.IsLeftButtonPressed) return;
        var p = pt.Position;
        Focus(FocusState.Pointer);
        _canvas.CapturePointer(e.Pointer);
        e.Handled = true;

        if (p.Y >= RulerHeight && HitClip(p, out var clip, out var edge))
        {
            Select(clip.Id);
            _dragClip = clip;
            _dragTrack = clip.Track;
            _dragStart = clip.Start;
            _grabOffset = T(p.X) - clip.Start;
            _mode = edge switch { < 0 => DragMode.TrimLeft, > 0 => DragMode.TrimRight, _ => DragMode.Move };
            _dragEdge = _mode == DragMode.TrimLeft ? clip.Start : clip.End;
            return;
        }

        if (p.Y >= RulerHeight) Select(null);
        _mode = DragMode.Scrub;
        Scrub(p.X);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;
        switch (_mode)
        {
            case DragMode.None:
                ProtectedCursor = InputSystemCursor.Create(
                    p.Y >= RulerHeight && HitClip(p, out _, out var edge) && edge != 0
                        ? InputSystemCursorShape.SizeWestEast
                        : InputSystemCursorShape.Arrow);
                return;
            case DragMode.Scrub:
                Scrub(p.X);
                break;
            case DragMode.Move when _dragClip is { } c && _clipVisuals.TryGetValue(c.Id, out var v):
            {
                var start = SnapMove(T(p.X) - _grabOffset, c);
                _dragStart = start < TimeSpan.Zero ? TimeSpan.Zero : start;
                _dragTrack = TrackFromY(p.Y);
                bool ok = _timeline!.CanPlace(_dragTrack, _dragStart, c.Duration, c.Id);
                Canvas.SetLeft(v, X(_dragStart));
                Canvas.SetTop(v, TrackY(_dragTrack) + 2);
                v.Opacity = ok ? 0.85 : 0.45;
                break;
            }
            case DragMode.TrimLeft or DragMode.TrimRight when _dragClip is { } c && _clipVisuals.TryGetValue(c.Id, out var v):
            {
                _dragEdge = SnapTime(T(p.X), c.Id);
                if (_mode == DragMode.TrimLeft)
                {
                    var s = _dragEdge > c.End - Timeline.MinClipDuration ? c.End - Timeline.MinClipDuration : _dragEdge;
                    Canvas.SetLeft(v, X(s));
                    v.Width = Math.Max(2, X(c.End) - X(s));
                }
                else
                {
                    var end = _dragEdge < c.Start + Timeline.MinClipDuration ? c.Start + Timeline.MinClipDuration : _dragEdge;
                    v.Width = Math.Max(2, X(end) - X(c.Start));
                }
                break;
            }
        }
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var mode = _mode;
        var clip = _dragClip;
        _mode = DragMode.None;
        _dragClip = null;
        _canvas.ReleasePointerCapture(e.Pointer);
        if (_timeline is null || clip is null) return;

        bool changed = mode switch
        {
            DragMode.Move => _timeline.MoveClip(clip.Id, _dragTrack, _dragStart),
            DragMode.TrimLeft => _timeline.TrimStart(clip.Id, _dragEdge),
            DragMode.TrimRight => _timeline.TrimEnd(clip.Id, _dragEdge),
            _ => false,
        };
        if (mode == DragMode.Move && !changed && (_dragTrack != clip.Track || _dragStart != clip.Start))
            EditRejected?.Invoke(this, "Die Zielposition ist bereits belegt.");
        if (!changed) Rebuild(); // Vorschau-Verschiebung zurücksetzen
    }

    private void CancelDrag()
    {
        if (_mode is DragMode.Move or DragMode.TrimLeft or DragMode.TrimRight) Rebuild();
        _mode = DragMode.None;
        _dragClip = null;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_canvas);
        int delta = pt.Properties.MouseWheelDelta;
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl)
        {
            // Zoom um die Mausposition: die Zeit unter dem Zeiger bleibt stehen.
            var anchor = T(pt.Position.X);
            double viewX = pt.Position.X - _scroll.HorizontalOffset;
            PixelsPerSecond = _pps * (delta > 0 ? 1.25 : 0.8);
            _scroll.UpdateLayout();
            _scroll.ChangeView(Math.Max(0, X(anchor) - viewX), null, null, true);
        }
        else
        {
            _scroll.ChangeView(Math.Max(0, _scroll.HorizontalOffset - delta), null, null, true);
        }
        e.Handled = true;
    }

    // ---------- Drag & Drop aus dem Medienpool ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = TrackFromY(e.GetPosition(_canvas).Y) == Timeline.OverlayTrack ? "Auf V2 (Overlay) legen" : "Auf V1 legen";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        var p = e.GetPosition(_canvas);
        var deferral = e.GetDeferral();
        try
        {
            var text = await e.DataView.GetTextAsync();
            if (text.StartsWith(DragPrefix, StringComparison.Ordinal) && Guid.TryParse(text[DragPrefix.Length..], out var id))
                AssetDropped?.Invoke(this, new AssetDroppedEventArgs(id, TrackFromY(p.Y), SnapTime(T(p.X), null)));
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---------- Hilfen ----------

    private void Select(Guid? id)
    {
        if (_selected == id) return;
        _selected = id;
        Rebuild();
        SelectionChanged?.Invoke(this, id);
    }

    private void Scrub(double x)
    {
        var t = Timecode.SnapToFrame(T(Math.Max(0, x)), FrameRate);
        Playhead = t;
        PlayheadScrubbed?.Invoke(this, t);
    }

    /// <summary>Clip unter dem Punkt; edge = -1 linke Kante, +1 rechte Kante, 0 Mitte.</summary>
    private bool HitClip(Point p, out TimelineClip clip, out int edge)
    {
        clip = null!;
        edge = 0;
        if (_timeline is null) return false;
        int track = TrackFromY(p.Y);
        double y = TrackY(track);
        if (p.Y < y || p.Y > y + TrackHeight) return false;

        foreach (var c in _timeline.GetClips(track))
        {
            double l = X(c.Start), r = X(c.End);
            double grab = Math.Min(EdgeGrab, (r - l) / 3);
            if (p.X < l - grab || p.X > r + grab) continue;
            clip = c;
            edge = p.X <= l + grab ? -1 : p.X >= r - grab ? 1 : 0;
            return true;
        }
        return false;
    }

    private TimeSpan SnapMove(TimeSpan start, TimelineClip c)
    {
        var snappedStart = SnapTime(start, c.Id);
        if (snappedStart != Timecode.SnapToFrame(start, FrameRate)) return snappedStart;
        var end = start + c.Duration;
        var snappedEnd = SnapTime(end, c.Id);
        return snappedEnd - c.Duration;
    }

    private TimeSpan SnapTime(TimeSpan t, Guid? ignore)
    {
        var framed = Timecode.SnapToFrame(t < TimeSpan.Zero ? TimeSpan.Zero : t, FrameRate);
        return _timeline?.Snap(framed, TimeSpan.FromSeconds(SnapPixels / _pps), ignore, _playhead) ?? framed;
    }

    private static double TrackY(int track) => RulerHeight + (track == Timeline.OverlayTrack ? 0 : TrackHeight + TrackGap);
    private static int TrackFromY(double y) => y < RulerHeight + TrackHeight + TrackGap / 2 ? Timeline.OverlayTrack : Timeline.MainTrack;
    private double X(TimeSpan t) => t.TotalSeconds * _pps;
    private TimeSpan T(double x) => TimeSpan.FromSeconds(Math.Max(0, x) / _pps);

    private void Place(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        _canvas.Children.Add(e);
    }

    private static Border TrackHeader(string name, string subtitle, string colorKey)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Opacity = 0.7 });
        return new Border
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 0, 6, 0),
            Background = ThemeBrush("ControlFillColorSecondaryBrush"),
            BorderBrush = Brush(colorKey, Colors.Gray),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Child = panel,
        };
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b ? b : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));

    private static Color ThemeColor(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var v)
            ? v switch { SolidColorBrush b => b.Color, Color c => c, _ => fallback }
            : fallback;

    private static SolidColorBrush Brush(string key, Color fallback) => new(ThemeColor(key, fallback));

    private static Color Lighten(Color c, double amount) => Color.FromArgb(c.A,
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));
}
