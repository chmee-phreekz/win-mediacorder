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
        // Robustheit: unbehandelte Fehler protokollieren und anzeigen, statt die App (und eine laufende Aufnahme) abzuschießen.
        Services.ErrorReporter.RegisterGlobalHandlers(this);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services.ErrorReporter.Initialize(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        Services.ErrorReporter.Log(null, "App gestartet");
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
