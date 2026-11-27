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

        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        ContentFrame.Content = tag == "edit" ? _editPage : _recordPage;
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
