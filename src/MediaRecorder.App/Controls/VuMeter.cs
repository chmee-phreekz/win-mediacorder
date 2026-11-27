using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace MediaRecorder.App.Controls;

/// <summary>
/// Senkrechte Stereo-Pegelanzeige (dBFS, -60 … 0) mit Spitzenwert-Haltelinie und Übersteuerungsanzeige.
/// Grün bis -18 dB, gelb bis -6 dB, rot darüber. Reines Code-Control, wird über SetLevels gefüttert (UI-Thread).
/// </summary>
public sealed class VuMeter : UserControl
{
    private const double MinDb = -60;
    private const double ClipDb = -0.5;
    private static readonly double[] ScaleMarks = [0, -6, -12, -18, -24, -36, -48, -60];

    private readonly Grid _root = new();
    private readonly Bar _left;
    private readonly Bar _right;
    private readonly Border _clipLamp;
    private readonly TextBlock _peakText;
    private readonly Canvas _scale = new() { Width = 22 };
    private long _clipUntil;

    public VuMeter()
    {
        _left = new Bar();
        _right = new Bar();

        _clipLamp = new Border
        {
            Height = 8, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 0, 4),
            Background = new SolidColorBrush(Color.FromArgb(60, 229, 72, 77)),
        };
        ToolTipService.SetToolTip(_clipLamp, "Übersteuerung");
        _peakText = new TextBlock
        {
            FontSize = 11, FontFamily = new FontFamily("Consolas"), HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0), Text = "-∞",
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumnSpan(_clipLamp, 3);
        _root.Children.Add(_clipLamp);
        Grid.SetRow(_left.Root, 1);
        _root.Children.Add(_left.Root);
        Grid.SetRow(_right.Root, 1); Grid.SetColumn(_right.Root, 2);
        _root.Children.Add(_right.Root);
        Grid.SetRow(_scale, 1); Grid.SetColumn(_scale, 3);
        _root.Children.Add(_scale);
        Grid.SetRow(_peakText, 2); Grid.SetColumnSpan(_peakText, 4);
        _root.Children.Add(_peakText);

        Content = _root;
        _scale.SizeChanged += (_, _) => DrawScale();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "Audiopegel");
    }

    /// <summary>Neue Messwerte in dBFS (UI-Thread).</summary>
    public void SetLevels(double leftPeakDb, double rightPeakDb, double leftRmsDb, double rightRmsDb)
    {
        var now = Environment.TickCount64;
        _left.Update(leftPeakDb, leftRmsDb, now);
        _right.Update(rightPeakDb, rightRmsDb, now);

        var maxPeak = Math.Max(leftPeakDb, rightPeakDb);
        if (maxPeak >= ClipDb) _clipUntil = now + 2000;
        _clipLamp.Background = new SolidColorBrush(now < _clipUntil
            ? Color.FromArgb(255, 229, 72, 77)
            : Color.FromArgb(60, 229, 72, 77));

        var hold = Math.Max(_left.HoldDb, _right.HoldDb);
        _peakText.Text = hold <= MinDb ? "-∞" : $"{hold:0.0}";
    }

    public void Reset() => SetLevels(-90, -90, -90, -90);

    private void DrawScale()
    {
        _scale.Children.Clear();
        var h = _scale.ActualHeight;
        if (h <= 0) return;
        var brush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        foreach (var db in ScaleMarks)
        {
            var y = (1 - Norm(db)) * h;
            var t = new TextBlock { Text = db == 0 ? "0" : db.ToString("0"), FontSize = 9, Foreground = brush };
            Canvas.SetLeft(t, 3);
            Canvas.SetTop(t, Math.Clamp(y - 6, 0, h - 12));
            _scale.Children.Add(t);
        }
    }

    private static double Norm(double db) => Math.Clamp((db - MinDb) / -MinDb, 0, 1);

    /// <summary>Ein Kanal: Farbverlauf als Hintergrund, eine Abdeckung von oben zeigt den Pegel, dazu die Haltelinie.</summary>
    private sealed class Bar
    {
        private readonly Rectangle _cover;
        private readonly Rectangle _rmsLine;
        private readonly Rectangle _holdLine;
        private readonly Grid _bar;
        private double _displayDb = -90;
        private double _rms = -90;
        private long _holdSince;

        public Grid Root { get; }
        public double HoldDb { get; private set; } = -90;

        public Bar()
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 46, 160, 67), Offset = 0 });
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 46, 160, 67), Offset = Norm(-18) });
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 230, 190, 40), Offset = Norm(-12) });
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 240, 150, 40), Offset = Norm(-6) });
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 229, 72, 77), Offset = Norm(-3) });
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 229, 72, 77), Offset = 1 });

            _bar = new Grid { Background = gradient, CornerRadius = new CornerRadius(2) };
            _cover = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(235, 24, 24, 26)),
                VerticalAlignment = VerticalAlignment.Top,
            };
            _rmsLine = new Rectangle
            {
                Height = 1, VerticalAlignment = VerticalAlignment.Top,
                Fill = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
            };
            _holdLine = new Rectangle
            {
                Height = 2, VerticalAlignment = VerticalAlignment.Top,
                Fill = new SolidColorBrush(Colors.White),
            };
            _bar.Children.Add(_cover);
            _bar.Children.Add(_rmsLine);
            _bar.Children.Add(_holdLine);
            _bar.SizeChanged += (_, _) => Layout();

            Root = new Grid { MinWidth = 10 };
            Root.Children.Add(_bar);
        }

        public void Update(double peakDb, double rmsDb, long now)
        {
            // Schneller Anstieg, langsamer Abfall (ca. 20 dB/s bei 30 Meldungen/s) wie bei PPM-Anzeigen.
            _displayDb = peakDb > _displayDb ? peakDb : Math.Max(peakDb, _displayDb - 0.7);
            // Spitzenwert 1,5 s halten, danach langsam sinken lassen.
            if (peakDb >= HoldDb) { HoldDb = peakDb; _holdSince = now; }
            else if (now - _holdSince > 1500) HoldDb = Math.Max(peakDb, HoldDb - 1.5);
            _rms = rmsDb;
            Layout();
        }

        private void Layout()
        {
            var h = _bar.ActualHeight;
            if (h <= 0) return;
            _cover.Height = Math.Max(0, (1 - Norm(_displayDb)) * h);
            _rmsLine.Margin = new Thickness(0, Math.Clamp((1 - Norm(_rms)) * h, 0, h - 1), 0, 0);
            _rmsLine.Visibility = _rms > MinDb ? Visibility.Visible : Visibility.Collapsed;
            _holdLine.Margin = new Thickness(0, Math.Clamp((1 - Norm(HoldDb)) * h, 0, h - 2), 0, 0);
            _holdLine.Visibility = HoldDb > MinDb ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
