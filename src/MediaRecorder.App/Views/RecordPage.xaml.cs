using System;
using System.ComponentModel;
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
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // NavigationCacheMode=Required: Seite bleibt beim Wechsel zur Schnittseite bestehen,
        // eine laufende Aufnahme läuft also weiter.
        if (_initialized) return;
        _initialized = true;
        await ViewModel.InitializeAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordViewModel.PreviewPlayer))
            Preview.SetMediaPlayer(ViewModel.PreviewPlayer);
    }

    private async void PickFolder_Click(object sender, RoutedEventArgs e)
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
