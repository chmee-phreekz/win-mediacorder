using System;
using Microsoft.UI.Xaml;

namespace MediaRecorder.App;

public partial class App : Application
{
    /// <summary>Hauptfenster, u. a. für Datei-/Ordnerauswahl (InitializeWithWindow) in der unpackaged App.</summary>
    public static Window MainWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Robustheit: unbehandelte UI-Fehler protokollieren statt die App (und eine laufende Aufnahme) abzuschießen.
            System.Diagnostics.Debug.WriteLine($"Unbehandelter Fehler: {e.Exception}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
