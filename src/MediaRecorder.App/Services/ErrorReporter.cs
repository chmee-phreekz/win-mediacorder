using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MediaRecorder.App.Services;

/// <summary>
/// Zentrale Fehlerbehandlung: schreibt in %LOCALAPPDATA%\MediaRecorder\logs\app-JJJJ-MM-TT.log
/// und zeigt dem Benutzer eine Meldung, statt die App abstürzen zu lassen.
/// Thread-sicher, darf aus beliebigen Threads aufgerufen werden.
/// </summary>
public static class ErrorReporter
{
    private static readonly object LogLock = new();
    private static DispatcherQueue? _ui;
    private static int _dialogOpen;

    /// <summary>
    /// Als MSIX-Paket liegt das Protokoll im Paketordner (%LOCALAPPDATA%\Packages\MediaRecorder.App_…\LocalState\logs),
    /// sonst unter %LOCALAPPDATA%\MediaRecorder\logs.
    /// </summary>
    public static string LogFolder { get; } = ResolveLogFolder();

    private static string ResolveLogFolder()
    {
        try
        {
            return Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "logs");
        }
        catch
        {
            // Ohne Paketidentität (unpackaged) wirft ApplicationData.Current.
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaRecorder", "logs");
        }
    }

    public static string CurrentLogFile => Path.Combine(LogFolder, $"app-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>Einmal beim Start auf dem UI-Thread aufrufen.</summary>
    public static void Initialize(DispatcherQueue ui) => _ui = ui;

    public static void Log(Exception? ex, string context)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(LogFolder);
                File.AppendAllText(CurrentLogFile,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [T{Environment.CurrentManagedThreadId}] {context}{Environment.NewLine}" +
                    (ex is null ? "" : ex + Environment.NewLine) + Environment.NewLine);
            }
        }
        catch { /* Logging darf nie selbst einen Fehler auslösen */ }
        System.Diagnostics.Debug.WriteLine($"{context}: {ex}");
    }

    /// <summary>Protokolliert und zeigt eine Meldung im UI (höchstens ein Dialog gleichzeitig).</summary>
    public static void Report(Exception? ex, string context, string? userMessage = null)
    {
        Log(ex, context);
        var ui = _ui;
        if (ui is null) return;

        var text = userMessage ?? ex?.Message ?? context;
        ui.TryEnqueue(async () =>
        {
            if (Interlocked.Exchange(ref _dialogOpen, 1) == 1) return;
            try
            {
                var root = App.MainWindow?.Content?.XamlRoot;
                if (root is null) return;
                var dialog = new ContentDialog
                {
                    XamlRoot = root,
                    Title = "Ein Fehler ist aufgetreten",
                    Content = new TextBlock
                    {
                        Text = $"{text}\n\nDie App läuft weiter. Details stehen im Protokoll:\n{CurrentLogFile}",
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                    CloseButtonText = "OK",
                    RequestedTheme = ElementTheme.Dark,
                };
                await dialog.ShowAsync();
            }
            catch (Exception dialogEx)
            {
                Log(dialogEx, "Fehlerdialog konnte nicht angezeigt werden");
            }
            finally
            {
                Interlocked.Exchange(ref _dialogOpen, 0);
            }
        });
    }

    /// <summary>Registriert die globalen Handler. Aus dem App-Konstruktor aufrufen.</summary>
    public static void RegisterGlobalHandlers(Application app)
    {
        app.UnhandledException += (_, e) =>
        {
            // Nicht beenden: eine laufende Aufnahme soll weiterlaufen.
            e.Handled = true;
            Report(e.Exception, "Unbehandelter UI-Fehler: " + e.Message);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Report(e.Exception.Flatten().InnerException ?? e.Exception, "Unbeobachteter Task-Fehler");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Hier kann der Prozess nicht mehr gerettet werden, aber die Ursache landet im Protokoll.
            Log(e.ExceptionObject as Exception, $"Fataler Fehler (IsTerminating={e.IsTerminating})");
        };
    }
}
