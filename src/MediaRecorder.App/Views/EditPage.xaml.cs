using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaRecorder.App.Controls;
using MediaRecorder.App.Services.Editing;
using MediaRecorder.App.ViewModels;
using MediaRecorder.Core.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace MediaRecorder.App.Views;

public sealed partial class EditPage : Page
{
    public EditViewModel ViewModel { get; }

    public EditPage()
    {
        ViewModel = new EditViewModel(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        InitializeComponent();

        Preview.SetMediaPlayer(ViewModel.Player);
        FormatBox.SelectedItem = ViewModel.Format;

        TimelineView.Timeline = ViewModel.Timeline;
        TimelineView.FrameRate = ViewModel.Format.FrameRate;
        TimelineView.PlayheadScrubbed += (_, t) => ViewModel.Seek(t);
        TimelineView.SelectionChanged += (_, id) => ViewModel.SelectedClipId = id;
        TimelineView.AssetDropped += (_, e) => ViewModel.AddAsset(e.AssetId, e.Track, e.Time);
        TimelineView.EditRejected += (_, msg) => ViewModel.InfoMessage = msg;
        ApplyZoom(ZoomSlider.Value);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RegisterShortcuts();
    }

    // ---------- Verbindung ViewModel <-> Timeline-Control ----------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditViewModel.Playhead):
                TimelineView.Playhead = ViewModel.Playhead;
                if (ViewModel.IsPlaying) TimelineView.EnsurePlayheadVisible();
                break;
            case nameof(EditViewModel.SelectedClipId):
                TimelineView.SelectedClipId = ViewModel.SelectedClipId;
                break;
            case nameof(EditViewModel.InPoint):
            case nameof(EditViewModel.OutPoint):
                TimelineView.SetRange(ViewModel.InPoint, ViewModel.OutPoint);
                break;
            case nameof(EditViewModel.Format):
                if (!Equals(FormatBox.SelectedItem, ViewModel.Format)) FormatBox.SelectedItem = ViewModel.Format;
                TimelineView.FrameRate = ViewModel.Format.FrameRate;
                LayoutPreviewFrame();
                break;
        }
    }

    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatBox.SelectedItem is ProjectFormat f && f != ViewModel.Format)
            ViewModel.Format = f;
    }

    // ---------- Vorschau: Projektbild seitenrichtig und größtmöglich ----------

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutPreviewFrame();

    private void LayoutPreviewFrame()
    {
        var r = Geometry.AspectFit(ViewModel.Format.Width, ViewModel.Format.Height,
            PreviewHost.ActualWidth - 16, PreviewHost.ActualHeight - 16);
        PreviewFrame.Width = Math.Max(0, r.Width);
        PreviewFrame.Height = Math.Max(0, r.Height);
    }

    // ---------- Import ----------

    private async void Import_Click(object sender, RoutedEventArgs e) => await PickAndImportAsync();

    private async Task PickAndImportAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (var ext in MediaImportService.AllExtensions) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
            await ViewModel.ImportFilesAsync(files.ToList());
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Importieren";
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.OfType<StorageFile>().Where(f => MediaImportService.KindFromPath(f.Path) is not null).ToList();
            if (files.Count < items.Count)
                ViewModel.InfoMessage = "Einige Dateien wurden übersprungen (Ordner oder nicht unterstützter Typ).";
            await ViewModel.ImportFilesAsync(files);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---------- Medienpool ----------

    private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SelectedAsset = AssetList.SelectedItem as MediaAssetItem;

    private void AssetList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is MediaAssetItem item)
        {
            e.Data.SetText(TimelineControl.DragPrefix + item.Asset.Id);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        }
    }

    private void AssetList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        ViewModel.AddToMainTrackCommand.Execute(null);

    private void RemoveAsset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RemoveAssetCommand.Execute((sender as FrameworkElement)?.Tag as MediaAssetItem);

    public Visibility IsEmpty(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------- Zoom ----------

    private void ZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (TimelineView is not null) ApplyZoom(e.NewValue);
    }

    /// <summary>Logarithmischer Zoom: 0 → 2 px/s, 100 → 800 px/s.</summary>
    private void ApplyZoom(double slider) => TimelineView.PixelsPerSecond = 2 * Math.Pow(400, slider / 100);

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        TimelineView.ZoomToFit();
        ZoomSlider.ValueChanged -= ZoomSlider_ValueChanged;
        ZoomSlider.Value = Math.Log(TimelineView.PixelsPerSecond / 2) / Math.Log(400) * 100;
        ZoomSlider.ValueChanged += ZoomSlider_ValueChanged;
    }

    // ---------- Export ----------

    private async void Export_Click(object sender, RoutedEventArgs e) => await ShowExportAsync();

    private async Task ShowExportAsync()
    {
        if (!ViewModel.CanExport) return;
        var settings = ViewModel.DefaultExportSettings();
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var suggested = Path.Combine(videos, $"Schnitt_{DateTime.Now:yyyy-MM-dd_HHmm}{settings.FileExtension}");

        var dialog = new ExportDialog(settings, ViewModel.Format.AspectRatio, ViewModel.Timeline.Duration, suggested)
        {
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var target = dialog.TargetPath;
        if (File.Exists(target) && !await ConfirmAsync("Datei überschreiben?", $"„{Path.GetFileName(target)}“ existiert bereits.", "Überschreiben"))
            return;

        await ViewModel.ExportAsync(dialog.Settings, target);
    }

    // ---------- Tastenkürzel ----------

    private void RegisterShortcuts()
    {
        Add(VirtualKey.Space, VirtualKeyModifiers.None, () => ViewModel.PlayPauseCommand.Execute(null));
        Add(VirtualKey.S, VirtualKeyModifiers.None, () => ViewModel.SplitCommand.Execute(null));
        Add(VirtualKey.Delete, VirtualKeyModifiers.None, () => ViewModel.DeleteClipCommand.Execute(null));
        Add(VirtualKey.Delete, VirtualKeyModifiers.Shift, () => ViewModel.RippleDeleteClipCommand.Execute(null));
        Add(VirtualKey.I, VirtualKeyModifiers.None, () => ViewModel.SetInPointCommand.Execute(null));
        Add(VirtualKey.O, VirtualKeyModifiers.None, () => ViewModel.SetOutPointCommand.Execute(null));
        Add(VirtualKey.X, VirtualKeyModifiers.None, () => ViewModel.CutRangeCommand.Execute(null));
        Add(VirtualKey.M, VirtualKeyModifiers.None, () => ViewModel.ToggleMuteCommand.Execute(null));
        Add(VirtualKey.Left, VirtualKeyModifiers.None, () => ViewModel.StepBackCommand.Execute(null));
        Add(VirtualKey.Right, VirtualKeyModifiers.None, () => ViewModel.StepForwardCommand.Execute(null));
        Add(VirtualKey.Home, VirtualKeyModifiers.None, () => ViewModel.GoToStartCommand.Execute(null));
        Add(VirtualKey.End, VirtualKeyModifiers.None, () => ViewModel.GoToEndCommand.Execute(null));
        Add(VirtualKey.Z, VirtualKeyModifiers.Control, () => { if (ViewModel.CanUndo) ViewModel.UndoCommand.Execute(null); });
        Add(VirtualKey.Y, VirtualKeyModifiers.Control, () => { if (ViewModel.CanRedo) ViewModel.RedoCommand.Execute(null); });
        Add(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => { if (ViewModel.CanRedo) ViewModel.RedoCommand.Execute(null); });
        Add(VirtualKey.I, VirtualKeyModifiers.Control, () => _ = PickAndImportAsync());
        Add(VirtualKey.E, VirtualKeyModifiers.Control, () => _ = ShowExportAsync());
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var acc = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        acc.Invoked += (_, args) =>
        {
            // Tastenkürzel nicht auslösen, während in ein Textfeld getippt wird.
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or NumberBox or AutoSuggestBox) return;
            args.Handled = true;
            action();
        };
        KeyboardAccelerators.Add(acc);
    }

    // ---------- Lebenszyklus ----------

    private void ErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.ErrorMessage = null;
    private void InfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.InfoMessage = null;

    /// <summary>Fragt nach, wenn beim Schließen noch ein Export läuft. true = Schließen erlaubt.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!ViewModel.IsExporting) return true;
        return await ConfirmAsync("Export läuft", "Beim Beenden wird der laufende Export abgebrochen.", "Abbrechen und beenden");
    }

    public ValueTask ShutdownAsync() => ViewModel.DisposeAsync();

    private async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
            // Beim Schließen kann die Schnittseite gerade nicht sichtbar sein -> XamlRoot des Fensters nehmen.
            XamlRoot = XamlRoot ?? App.MainWindow.Content.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
