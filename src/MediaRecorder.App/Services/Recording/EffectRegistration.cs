using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MediaRecorder.App.Services.Recording;

/// <summary>
/// Registriert den Kompressor-Effekt als Windows-Runtime-Klasse in HKLM.
///
/// Hintergrund: Media Foundation (MediaCapture.AddAudioEffectAsync) liest die Registrierung eines Effekts
/// direkt aus der Registry. Die registrierungsfreie Variante über app.manifest reicht dafür nicht
/// ("Could not read key from registry"). Für eine unpackaged App ist daher ein Eintrag unter
/// HKLM\SOFTWARE\Microsoft\WindowsRuntime\ActivatableClassId nötig. Das Schreiben braucht Adminrechte;
/// die App startet sich dafür einmal selbst mit UAC-Abfrage (Argument --register-effect) und beendet
/// diese zweite Instanz sofort wieder.
///
/// Der Eintrag zeigt auf WinRT.Host.dll im Programmordner. Wird die App an einen anderen Ort verschoben
/// (z.B. Debug ↔ Release), muss erneut registriert werden; die App erkennt das selbst.
/// </summary>
public static class EffectRegistration
{
    public const string ClassId = "MediaRecorder.Effects.CompressorEffect";
    public const string RegisterArgument = "--register-effect";
    public const string UnregisterArgument = "--unregister-effect";

    private const string KeyPath = @"SOFTWARE\Microsoft\WindowsRuntime\ActivatableClassId\" + ClassId;

    public static string HostPath => Path.Combine(AppContext.BaseDirectory, "WinRT.Host.dll");

    /// <summary>True, wenn die Registrierung auf genau diesen Programmordner zeigt.</summary>
    public static bool IsRegisteredForThisInstall()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            return key?.GetValue("DllPath") is string path
                && string.Equals(Path.GetFullPath(path), Path.GetFullPath(HostPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Startet die App mit Adminrechten, um die Registrierung zu schreiben. Gibt null bei Erfolg zurück,
    /// sonst eine verständliche Fehlermeldung.
    /// </summary>
    public static async Task<string?> RequestRegistrationAsync()
    {
        if (!File.Exists(HostPath))
            return $"WinRT.Host.dll fehlt im Programmordner ({AppContext.BaseDirectory}). Bitte die Projektmappe neu erstellen.";

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return "Pfad der Anwendung unbekannt.";

        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, $"{RegisterArgument} \"{HostPath}\"")
            {
                UseShellExecute = true,
                Verb = "runas", // UAC-Abfrage
            });
            if (p is null) return "Registrierung konnte nicht gestartet werden.";
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) return $"Registrierung fehlgeschlagen (Code {p.ExitCode}). Details im Protokoll.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Die Adminabfrage wurde abgebrochen. Ohne Registrierung wird unkomprimiert aufgenommen.";
        }
        catch (Exception ex)
        {
            ErrorReporter.Log(ex, "Kompressor registrieren");
            return ex.Message;
        }

        return IsRegisteredForThisInstall() ? null : "Registrierung wurde nicht übernommen. Details im Protokoll.";
    }

    /// <summary>
    /// Wird in der elevierten Instanz aufgerufen (aus App.OnLaunched). Gibt den Prozess-Exitcode zurück.
    /// Behandelt nur die eigenen Argumente, sonst null.
    /// </summary>
    public static int? HandleCommandLine(string[] args)
    {
        int i = Array.FindIndex(args, a => a is RegisterArgument or UnregisterArgument);
        if (i < 0) return null;

        try
        {
            if (args[i] == UnregisterArgument)
            {
                Registry.LocalMachine.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
                ErrorReporter.Log(null, "Kompressor-Effekt deregistriert");
                return 0;
            }

            var hostPath = i + 1 < args.Length ? args[i + 1] : HostPath;
            hostPath = Path.GetFullPath(hostPath);
            // Nur WinRT.Host.dll aus dem eigenen Programmordner eintragen, nie einen beliebigen Pfad.
            if (!string.Equals(hostPath, Path.GetFullPath(HostPath), StringComparison.OrdinalIgnoreCase) || !File.Exists(hostPath))
            {
                ErrorReporter.Log(null, $"Registrierung abgelehnt, unerwarteter Pfad: {hostPath}");
                return 2;
            }

            using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
            key.SetValue("ActivationType", 0, RegistryValueKind.DWord); // 0 = InProcess
            key.SetValue("DllPath", hostPath, RegistryValueKind.String);
            key.SetValue("Threading", 0, RegistryValueKind.DWord);      // 0 = Both
            key.SetValue("TrustLevel", 0, RegistryValueKind.DWord);     // 0 = BaseTrust
            ErrorReporter.Log(null, $"Kompressor-Effekt registriert: {hostPath}");
            return 0;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log(ex, "Kompressor-Effekt registrieren (eleviert)");
            return 1;
        }
    }
}
