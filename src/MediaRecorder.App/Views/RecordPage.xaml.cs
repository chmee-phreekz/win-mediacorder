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
    private bool _initialized;

    public RecordPage()
    {
        ViewModel = new RecordViewModel(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.AudioLevelsChanged += (_, l) => Meter.SetLevels(l.LeftPeakDb, l.RightPeakDb, l.LeftRmsDb, l.RightRmsDb);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // NavigationCacheMode=Required: Seite bleibt beim Wechsel zur Schnittseite bestehen,
        // eine laufende Aufnahme läuft also weiter.
        // Beim Zurückkehren von der Schnittseite die Vorschau wieder verbinden.
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
    /// Beim Wechsel zur Schnittseite wird diese Seite aus dem Fenster genommen. Ein MediaPlayerElement,
    /// das außerhalb des sichtbaren Baums weiter ein Live-Kamerabild rendert, kann nativ abstürzen.
    /// Deshalb wird die Vorschau gelöst (Aufnahme läuft unverändert weiter) und beim Zurückkehren neu verbunden.
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isVisible = false;
        try
        {
            Preview.SetMediaPlayer(null);
            Meter.Reset();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log(ex, "Vorschau lösen");
        }
    }

    private bool _isVisible;

    private void AttachPreview()
    {
        _isVisible = true;
        try
        {
            if (!ReferenceEquals(Preview.MediaPlayer, ViewModel.PreviewPlayer))
                Preview.SetMediaPlayer(ViewModel.PreviewPlayer);
            if (ViewModel.PreviewPlayer?.Source is not null)
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

    private void ErrorBar_Closed(InfoBar sender, object args) => ViewModel.ErrorMessage = null;
    private void InfoBar_Closed(InfoBar sender, object args) => ViewModel.InfoMessage = null;

    /// <summary>Vom MainWindow beim Schließen aufrufen, damit eine laufende Aufnahme sauber finalisiert wird.</summary>
    public System.Threading.Tasks.ValueTask ShutdownAsync() => ViewModel.DisposeAsync();
}
