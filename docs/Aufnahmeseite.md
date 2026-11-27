# Aufnahmeseite (Version 1)

## Aufbau
- `Views/RecordPage.xaml` – Vorschau links (immer seitenrichtig, größtmöglich), Einstellungen rechts, Transportleiste unten.
- `ViewModels/RecordViewModel.cs` – Gerätewahl, Formate, Codec/Bitrate, Ziel, Zeit- und Dateigrößenanzeige.
- `Services/Recording/MediaCaptureRecordingBackend.cs` – Windows MediaCapture (Media Foundation), Hardware-Encoder wo vorhanden.
- `Services/Recording/CaptureDeviceService.cs` – Geräteliste und An-/Abstecken (DeviceWatcher).
- `Services/Recording/RecordingSettingsStore.cs` – letzte Einstellungen in `%LOCALAPPDATA%\MediaRecorder\record-settings.json`.
- Core: `Devices/CaptureDeviceInfo.cs`, `Recording/RecordingOptions.cs`.

## Robustheit
- Alle Start/Stop/Pause-Aufrufe serialisiert, Doppelklicks harmlos.
- Gerät abgezogen oder Treiberfehler: Datei wird so weit möglich abgeschlossen, Meldung mit Pfad.
- Freier Speicher wird vor dem Start und alle 2 s geprüft; unter 512 MB wird sauber gestoppt.
- Bestehende Dateien werden nie überschrieben.
- Fenster schließen während der Aufnahme finalisiert die Datei zuerst.
- Codecs werden vor dem Start gegen die auf dem System vorhandenen Encoder geprüft (z.B. HEVC).

## Annahmen, auf echter Hardware zu prüfen
1. Vorschau und Aufnahme gleichzeitig vom selben Stream (Geräte ohne eigenen Vorschau-Stream).
2. FLAC/PCM im MP4 über MediaCapture (wird zur Laufzeit abgefangen, falls nicht unterstützt).
3. Grabber, die nur DirectShow und kein Media Foundation anbieten, erscheinen nicht in der Liste.
4. MP4 ist bei einem harten Absturz (Stromausfall) nicht wiederherstellbar; MediaCapture kann kein fragmentiertes MP4/MKV schreiben.

## Testen
Visual Studio 2022 (17.12+) mit Workload „Windows-Anwendungsentwicklung“, `MediaRecorder.sln` öffnen, Plattform x64, F5.
Windows-Einstellungen > Datenschutz > Kamera und Mikrofon: Zugriff für Desktop-Apps erlauben.
