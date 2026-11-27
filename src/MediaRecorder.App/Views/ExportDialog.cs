using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaRecorder.Core.Encoding;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MediaRecorder.App.Views;

/// <summary>
/// Exportdialog mit Vorgaben und frei einstellbarem Container, Codec, Auflösung, Bildrate und Bitraten.
/// Ungültige Kombinationen werden sofort angezeigt; „Exportieren“ ist dann gesperrt.
/// </summary>
public sealed class ExportDialog : ContentDialog
{
    private static readonly double[] FrameRates = [23.976, 24, 25, 29.97, 30, 50, 59.94, 60];
    private static readonly int[] AudioBitrates = [96, 128, 160, 192, 256, 320];
    private static readonly int[] SampleRates = [44_100, 48_000];

    private readonly ComboBox _preset = new() { Header = "Vorgabe", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _container = new() { Header = "Container", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _videoCodec = new() { Header = "Videocodec", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _width = Num("Breite", 16, 7680, 2);
    private readonly NumberBox _height = Num("Höhe", 16, 4320, 2);
    private readonly ComboBox _fps = new() { Header = "Bildrate (fps)", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _videoBitrate = Num("Videobitrate (Mbit/s)", 0.1, 400, 0.5);
    private readonly ToggleSwitch _includeAudio = new() { Header = "Ton exportieren", IsOn = true };
    private readonly ComboBox _audioCodec = new() { Header = "Audiocodec", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _audioBitrate = new() { Header = "Audiobitrate (kbit/s)", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _sampleRate = new() { Header = "Abtastrate", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _path = new() { Header = "Zieldatei", IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar _validation = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly InfoBar _aspectHint = new() { Severity = InfoBarSeverity.Informational, IsClosable = false };
    private readonly TextBlock _estimate = new() { Opacity = 0.75 };

    private readonly double _projectAspect;
    private readonly TimeSpan _duration;
    private bool _loading;

    public ExportDialog(EncodingSettings initial, double projectAspect, TimeSpan duration, string suggestedPath)
    {
        _projectAspect = projectAspect;
        _duration = duration;

        Title = "Exportieren";
        PrimaryButtonText = "Exportieren";
        CloseButtonText = "Abbrechen";
        DefaultButton = ContentDialogButton.Primary;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        _preset.Items.Add("Benutzerdefiniert");
        foreach (var p in EncodingPresets.All) _preset.Items.Add(p);
        foreach (var c in Enum.GetValues<ContainerFormat>()) _container.Items.Add(c);
        foreach (var f in FrameRates) _fps.Items.Add(f);
        foreach (var b in AudioBitrates) _audioBitrate.Items.Add(b);
        foreach (var r in SampleRates) _sampleRate.Items.Add(r);

        var browse = new Button { Content = "Durchsuchen …", VerticalAlignment = VerticalAlignment.Bottom };
        browse.Click += Browse_Click;

        var pathRow = Row(_path, browse);
        ((Grid)pathRow).ColumnDefinitions[1].Width = GridLength.Auto;

        var panel = new StackPanel { Spacing = 12, MinWidth = 520 };
        panel.Children.Add(_preset);
        panel.Children.Add(Section("Video"));
        panel.Children.Add(Row(_container, _videoCodec));
        panel.Children.Add(Row(_width, _height));
        panel.Children.Add(Row(_fps, _videoBitrate));
        panel.Children.Add(Section("Audio"));
        panel.Children.Add(_includeAudio);
        panel.Children.Add(Row(_audioCodec, _audioBitrate));
        panel.Children.Add(_sampleRate);
        panel.Children.Add(Section("Ausgabe"));
        panel.Children.Add(pathRow);
        panel.Children.Add(_estimate);
        panel.Children.Add(_aspectHint);
        panel.Children.Add(_validation);
        Content = new ScrollViewer { Content = panel, MaxHeight = 640 };

        _path.Text = suggestedPath;
        Load(initial);

        _preset.SelectionChanged += (_, _) =>
        {
            if (!_loading && _preset.SelectedItem is EncodingPreset p) Load(p.Settings, keepPreset: true);
        };
        _container.SelectionChanged += (_, _) => { if (!_loading) { FillCodecs(); Changed(); } };
        foreach (var cb in new[] { _videoCodec, _fps, _audioCodec, _audioBitrate, _sampleRate })
            cb.SelectionChanged += (_, _) => Changed();
        foreach (var nb in new[] { _width, _height, _videoBitrate })
            nb.ValueChanged += (_, _) => Changed();
        _includeAudio.Toggled += (_, _) => Changed();

        PrimaryButtonClick += (_, args) =>
        {
            if (Validate().Count > 0) args.Cancel = true;
        };
    }

    public EncodingSettings Settings => Build();
    public string TargetPath => _path.Text;

    private void Load(EncodingSettings s, bool keepPreset = false)
    {
        _loading = true;
        try
        {
            if (!keepPreset)
                _preset.SelectedItem = EncodingPresets.All.FirstOrDefault(p => p.Settings == s) ?? (object)"Benutzerdefiniert";
            _container.SelectedItem = s.Container;
            FillCodecs();
            _videoCodec.SelectedItem = s.VideoCodec;
            _audioCodec.SelectedItem = s.AudioCodec;
            _width.Value = s.Width;
            _height.Value = s.Height;
            _fps.SelectedItem = FrameRates.OrderBy(f => Math.Abs(f - s.FrameRate)).First();
            _videoBitrate.Value = s.VideoBitrateKbps / 1000.0;
            _audioBitrate.SelectedItem = AudioBitrates.OrderBy(b => Math.Abs(b - s.AudioBitrateKbps)).First();
            _sampleRate.SelectedItem = SampleRates.Contains(s.AudioSampleRate) ? s.AudioSampleRate : 48_000;
            _includeAudio.IsOn = s.IncludeAudio;
        }
        finally
        {
            _loading = false;
        }
        Changed(fromPreset: keepPreset);
    }

    private void FillCodecs()
    {
        var container = _container.SelectedItem is ContainerFormat c ? c : ContainerFormat.Mp4;
        Refill(_videoCodec, EncodingSettings.VideoCodecsFor(container).Cast<object>());
        Refill(_audioCodec, EncodingSettings.AudioCodecsFor(container).Cast<object>());
        // Dateiendung an den Container anpassen
        if (!string.IsNullOrEmpty(_path.Text))
            _path.Text = Path.ChangeExtension(_path.Text, new EncodingSettings { Container = container }.FileExtension);
    }

    private static void Refill(ComboBox box, IEnumerable<object> items)
    {
        var previous = box.SelectedItem;
        box.Items.Clear();
        foreach (var i in items) box.Items.Add(i);
        box.SelectedItem = box.Items.Contains(previous!) ? previous : box.Items.FirstOrDefault();
    }

    private void Changed(bool fromPreset = false)
    {
        if (_loading) return;
        if (!fromPreset && _preset.SelectedItem is EncodingPreset p && p.Settings != Build())
        {
            _loading = true;
            _preset.SelectedIndex = 0; // "Benutzerdefiniert"
            _loading = false;
        }

        _audioCodec.IsEnabled = _audioBitrate.IsEnabled = _sampleRate.IsEnabled = _includeAudio.IsOn;
        var errors = Validate();
        _validation.Message = string.Join(Environment.NewLine, errors);
        _validation.IsOpen = errors.Count > 0;
        IsPrimaryButtonEnabled = errors.Count == 0;

        var s = Build();
        double exportAspect = s.Height > 0 ? s.Width / (double)s.Height : _projectAspect;
        _aspectHint.IsOpen = Math.Abs(exportAspect - _projectAspect) > 0.01;
        _aspectHint.Message = "Das Seitenverhältnis weicht vom Projektformat ab. Das Bild wird unverzerrt eingepasst, es entstehen schwarze Ränder.";

        double kbps = s.VideoBitrateKbps + (s.IncludeAudio ? s.AudioBitrateKbps : 0);
        double megabytes = kbps * 1000 / 8 * _duration.TotalSeconds / 1_000_000;
        _estimate.Text = $"Geschätzte Dateigröße: {(megabytes >= 1000 ? $"{megabytes / 1000:0.0} GB" : $"{megabytes:0} MB")} bei {_duration:hh\\:mm\\:ss} Länge";
    }

    private List<string> Validate()
    {
        var errors = Build().Validate().ToList();
        if (string.IsNullOrWhiteSpace(_path.Text) || !Path.IsPathRooted(_path.Text))
            errors.Add("Bitte eine Zieldatei wählen.");
        else if (!Directory.Exists(Path.GetDirectoryName(_path.Text)))
            errors.Add("Der Zielordner existiert nicht.");
        return errors;
    }

    private EncodingSettings Build() => new()
    {
        Container = _container.SelectedItem is ContainerFormat c ? c : ContainerFormat.Mp4,
        VideoCodec = _videoCodec.SelectedItem is VideoCodec v ? v : VideoCodec.H264,
        Width = SafeInt(_width.Value),
        Height = SafeInt(_height.Value),
        FrameRate = _fps.SelectedItem is double f ? f : 30,
        VideoBitrateKbps = SafeInt(_videoBitrate.Value * 1000),
        IncludeAudio = _includeAudio.IsOn,
        AudioCodec = _audioCodec.SelectedItem is AudioCodec a ? a : AudioCodec.Aac,
        AudioBitrateKbps = _audioBitrate.SelectedItem is int ab ? ab : 192,
        AudioSampleRate = _sampleRate.SelectedItem is int sr ? sr : 48_000,
    };

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var container = _container.SelectedItem is ContainerFormat c ? c : ContainerFormat.Mp4;
        var ext = new EncodingSettings { Container = container }.FileExtension;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(_path.Text),
        };
        picker.FileTypeChoices.Add(container == ContainerFormat.Wmv ? "Windows Media Video" : "MPEG-4-Video", new List<string> { ext });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            _path.Text = file.Path;
            Changed();
        }
    }

    private static int SafeInt(double v) => double.IsNaN(v) ? 0 : (int)Math.Round(v);

    private static NumberBox Num(string header, double min, double max, double step) => new()
    {
        Header = header,
        Minimum = min,
        Maximum = max,
        SmallChange = step,
        LargeChange = step * 10,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(0, 8, 0, 0),
    };

    private static FrameworkElement Row(FrameworkElement left, FrameworkElement right)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }
}
