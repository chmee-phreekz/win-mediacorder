using System;
using System.Linq;
using MediaRecorder.App.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MediaRecorder.App;

public sealed partial class MainWindow : Window
{
    // Beide Seiten leben über die ganze Sitzung: eine laufende Aufnahme oder ein Export
    // läuft weiter, wenn man die Seite wechselt.
    private readonly RecordPage _recordPage = new();
    private readonly EditPage _editPage = new();
    private bool _shutdownDone;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1600, 960));
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Closing += OnClosing;

        _editPage.Visibility = Visibility.Collapsed;
        PageHost.Children.Add(_recordPage);
        PageHost.Children.Add(_editPage);

        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        try
        {
            var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
            Services.ErrorReporter.Log(null, $"Seitenwechsel: {tag}");
            bool edit = tag == "edit";
            // Nur Sichtbarkeit umschalten, nie aus dem Baum entfernen (siehe MainWindow.xaml).
            _recordPage.Visibility = edit ? Visibility.Collapsed : Visibility.Visible;
            _editPage.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
            _recordPage.SetActive(!edit);
            _editPage.SetActive(edit);
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Report(ex, "Seitenwechsel");
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownDone) return;
        args.Cancel = true;

        if (!await _editPage.ConfirmCloseAsync())
            return;

        try
        {
            // Laufende Aufnahme sauber finalisieren, sonst ist die MP4-Datei unbrauchbar.
            await _recordPage.ShutdownAsync();
            await _editPage.ShutdownAsync();
        }
        finally
        {
            _shutdownDone = true;
            Close();
        }
    }
}
