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

## Fehlerbehandlung und Protokoll
- Unbehandelte Fehler (UI, Tasks) werden abgefangen, im Dialog gemeldet und protokolliert; die App läuft weiter.
- Protokoll: `%LOCALAPPDATA%\MediaRecorder\logs\app-JJJJ-MM-TT.log`.
- Die Vorschau nutzt einen dauerhaften MediaPlayer, beim Gerätewechsel wird nur die Quelle getauscht
  (das Verwerfen eines angezeigten Players führte zum nativen Absturz 0xC000027B).
- Lässt sich ein Audiogerät nicht öffnen, läuft die Vorschau ohne Ton weiter, mit Hinweis.

## VU-Meter
- Rechts neben dem Videobild, Stereo, -60 … 0 dBFS. Balken = Spitzenpegel (schneller Anstieg, langsamer Abfall),
  dünne Linie = RMS, weiße Linie = gehaltene Spitze (1,5 s), rote Lampe oben = Übersteuerung (≥ -0,5 dBFS, 2 s).
- Misst über einen eigenen AudioGraph im geteilten Modus, unabhängig von der Aufnahme. Bei einem Fehler wird nur die Anzeige ausgeblendet.

## Seitenwechsel
- Beim Wechsel zur Schnittseite wird die Live-Vorschau vom Fenster gelöst und beim Zurückkehren neu verbunden.
  Eine laufende Aufnahme läuft dabei weiter.

## Kompressor
- Regler auf der Aufnahmeseite: Threshold (-60 … 0 dBFS), Ratio (1:1 … 20:1), Gain (-12 … +24 dB). Bleibt während der Aufnahme bedienbar.
- Attack, Release und Knie nur im Code: Konstanten `AttackMs`, `ReleaseMs`, `KneeDb` in `src/MediaRecorder.Core/Audio/Compressor.cs`.
- Wirkt auf die Aufnahme als Media-Foundation-Audioeffekt (`src/MediaRecorder.Effects`, WinRT-Komponente),
  registriert ohne Installation über `app.manifest` (activatableClass → WinRT.Host.dll).
- Das VU-Meter rechnet denselben Kompressor mit denselben Einstellungen und zeigt so sofort das komprimierte Signal
  sowie die aktuelle Pegelreduktion.
- Lässt sich der Effekt nicht einbinden, erscheint eine Warnung; die Aufnahme läuft dann unkomprimiert weiter.
- **Registrierung (unpackaged):** Media Foundation findet den Effekt nur über HKLM\SOFTWARE\Microsoft\WindowsRuntime\ActivatableClassId.
  Ist er nicht registriert, zeigt die Kompressor-Karte „Jetzt registrieren (Adminrechte)“. Die App startet sich dann einmal mit
  UAC-Abfrage (`MediaRecorder.exe --register-effect`), schreibt den Eintrag (zeigt auf WinRT.Host.dll im Programmordner) und
  bindet den Effekt neu ein. Nach Verschieben der App (z.B. Debug ↔ Release) erneut registrieren. Entfernen: `MediaRecorder.exe --unregister-effect` als Admin.

## LUT
- Karte „LUT“ auf der Aufnahmeseite: .cube-Datei wählen (3D, auch 1D), ein/aus, entfernen. Auch während der Aufnahme schaltbar. Wird gespeichert.
- **Aufnahme:** Video-Effekt `MediaRecorder.Effects.LutEffect` im Record-Stream von MediaCapture (NV12, CPU, mehrkernig,
  vorberechnete YUV-Tabellen; BT.709 ab 720p, sonst BT.601, begrenzter Bereich). Gleiche HKLM-Registrierung wie der Kompressor.
- **Vorschau:** Bei aktiver LUT läuft die Vorschau über einen MediaFrameReader (BGRA8, max. 1280 px breit) mit derselben LUT,
  sonst über den MediaPlayer. Auf echter Hardware prüfen: Sieht die Vorschau doppelt „gegradet“ aus, wirkt der Effekt bereits
  auf den Vorschau-Stream; dann LUT nur noch an einer Stelle anwenden.
- Ist der Effekt nicht eingebunden, zeigt die Karte eine Warnung (mit Registrieren-Knopf); die LUT wirkt dann nur in der Vorschau.
