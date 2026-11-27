# MediaRecorder für Windows

WinUI-3-Anwendung (C#, Dark Mode) zum Aufnehmen und einfachen Schneiden von Video.

- **Aufnahme:** wählbarer Video-Input (USB, Webcam, Grabber) und Audio-Input (Soundkarte, USB), Codec und Bitrate einstellbar. Basis: Windows `MediaCapture` (Media Foundation).
- **Schnitt:** 2-Spur-Timeline mit Vorschau, Teilen/Ausschneiden, Import in korrektem Seitenverhältnis, Export mit Codec-Einstellungen. Basis: WinRT `MediaComposition`.

## Aufbau

- `src/MediaRecorder.App` – WinUI-3-Oberfläche (Windows App SDK, als MSIX-Paket)
- `src/MediaRecorder.Effects` – Kompressor und LUT als Media-Foundation-Effekte (WinRT-Komponente, registriert in `Package.appxmanifest`)
- `src/MediaRecorder.Core` – plattformunabhängige Logik (net8.0)
- `tests/MediaRecorder.Core.Tests` – xUnit-Tests für Core
- `docs/` – Beschreibung der Aufnahme- und Schnittseite

## Bauen

Windows 10/11, Visual Studio 2022 oder 2026 mit Workload „Windows-Anwendungsentwicklung“ und .NET 8 SDK. Der C++-Workload ist nicht nötig (siehe `Directory.Build.targets`).
Windows-Einstellungen > System > Für Entwickler: **Entwicklermodus** einschalten. F5 installiert das MSIX-Paket lokal und startet es. `MediaRecorder.sln` öffnen und `MediaRecorder.App` starten.

Tests: `dotnet test tests/MediaRecorder.Core.Tests`

## Version

Zentral in `Version.props`: Hauptversion als Buchstabe mit Namen (A = Agora), Subversion bei jedem Push um 0.01 erhöhen.
Angezeigt in der Titelleiste, z.B. „Media Recorder · Agora 1.50“.
