using System;
using System.ComponentModel;
using MediaRecorder.App.Services;
using MediaRecorder.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace MediaRecorder.App.Views;

public sealed partial class RecordPage : Page
{
    public RecordViewModel ViewModel { get; }
    private readonly Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource _lutSource = new();
    private int _lutFramePending;
    private bool _initialized;

    public RecordPage()
    {
        ViewModel = new RecordViewModel(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.AudioLevelsChanged += (_, l) => Meter.SetLevels(l.LeftPeakDb, l.RightPeakDb, l.LeftRmsDb, l.RightRmsDb);
        ViewModel.LutPreviewFrame += OnLutPreviewFrame;
        LutPreviewImage.Source = _lutSource;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Die Seite bleibt dauerhaft im Fenster (MainWindow blendet nur um), Loaded kommt also nur einmal.
        AttachPreview();
        if (_initialized) return;
        _initialized = true;
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex, "Aufnahmeseite initialisieren");
        }
    }

    /// <summary>
    /// Vom Hauptfenster beim Seitenwechsel aufgerufen. Die Seite bleibt im visuellen Baum (nur ausgeblendet);
    /// solange sie verborgen ist, ruht die Vorschau, die Aufnahme läuft unverändert weiter.
    /// Der MediaPlayer bleibt dabei immer am MediaPlayerElement, es wird nur pausiert und fortgesetzt.
    /// </summary>
    public void SetActive(bool active)
    {
        try
        {
            if (active)
            {
                AttachPreview();
                _ = ViewModel.ResumePreviewAsync(); // pausierte Live-Vorschau neu aufbauen, sonst bleibt sie schwarz
            }
            else
            {
                _isVisible = false;
                if (!ViewModel.IsLutPreviewActive) ViewModel.PreviewPlayer?.Pause();
                Meter.Reset();
            }
        }
        catch (Exception ex)
        {
            ErrorReporter.Log(ex, "Seitenwechsel Aufnahme");
        }
    }

    /// <summary>
    /// Wird nur noch beim Schließen des Fensters ausgelöst (die Seite bleibt sonst im Baum). Ein MediaPlayerElement,
    /// das außerhalb des sichtbaren Baums weiter ein Live-Kamerabild rendert, kann nativ abstürzen.
    /// Deshalb wird die Vorschau gelöst (Aufnahme läuft unverändert weiter) und beim Zurückkehren neu verbunden.
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isVisible = false;
        // Den MediaPlayer bewusst NICHT vom MediaPlayerElement lösen: Lösen und erneutes Verbinden
        // war die Ursache des Absturzes beim Zurückwechseln von der Schnittseite.
        try { ViewModel.PreviewPlayer?.Pause(); }
        catch (Exception ex) { ErrorReporter.Log(ex, "Vorschau pausieren"); }
    }

    private bool _isVisible;

    private void AttachPreview()
    {
        _isVisible = true;
        try
        {
            if (!ReferenceEquals(Preview.MediaPlayer, ViewModel.PreviewPlayer))
                Preview.SetMediaPlayer(ViewModel.PreviewPlayer);
            if (ViewModel.PreviewPlayer?.Source is not null && !ViewModel.IsLutPreviewActive)
                ViewModel.PreviewPlayer.Play();
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex, "Vorschau verbinden");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordViewModel.IsAudioMeterVisible) && !ViewModel.IsAudioMeterVisible)
            Meter.Reset();
        if (e.PropertyName != nameof(RecordViewModel.PreviewPlayer) || !_isVisible) return;
        AttachPreview();
    }

    private async void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        try { await PickFolderAsync(); }
        catch (Exception ex) { ErrorReporter.Report(ex, "Zielordner wählen"); }
    }

    private async System.Threading.Tasks.Task PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");

        // Unpackaged WinUI 3: Picker muss mit dem Fensterhandle verknüpft werden.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            ViewModel.SetOutputFolder(folder.Path);
    }

    /// <summary>x:Bind-Funktion: MediaPlayer-Vorschau ausblenden, solange die LUT-Vorschau läuft.</summary>
    public Visibility InvertVisibility(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Neues LUT-Vorschaubild (Hintergrund-Thread). Ist noch eins in Arbeit, wird dieses verworfen.</summary>
    private void OnLutPreviewFrame(object? sender, Windows.Graphics.Imaging.SoftwareBitmap bmp)
    {
        if (!_isVisible || System.Threading.Interlocked.Exchange(ref _lutFramePending, 1) == 1)
        {
            bmp.Dispose();
            return;
        }
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try { await _lutSource.SetBitmapAsync(bmp); }
                catch (Exception ex) { ErrorReporter.Log(ex, "LUT-Vorschau anzeigen"); }
                finally
                {
                    bmp.Dispose();
                    System.Threading.Interlocked.Exchange(ref _lutFramePending, 0);
                }
            }))
        {
            bmp.Dispose();
            System.Threading.Interlocked.Exchange(ref _lutFramePending, 0);
        }
    }

    private async void PickLut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".cube");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                await ViewModel.SetLutFileAsync(file.Path);
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex, "LUT wählen");
        }
    }

    /// <summary>x:Bind-Funktion: Regler gedimmt anzeigen, wenn der Kompressor aus ist.</summary>
    public double CompressorOpacity(bool enabled) => enabled ? 1.0 : 0.5;

    private void ErrorBar_Closed(InfoBar sender, object args) => ViewModel.ErrorMessage = null;
    private void InfoBar_Closed(InfoBar sender, object args) => ViewModel.InfoMessage = null;

    /// <summary>Vom MainWindow beim Schließen aufrufen, damit eine laufende Aufnahme sauber finalisiert wird.</summary>
    public System.Threading.Tasks.ValueTask ShutdownAsync() => ViewModel.DisposeAsync();
}
