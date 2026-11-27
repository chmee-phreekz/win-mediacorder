# Schnittseite (Seite 2) – erste Version

## Bedienung

| Aktion | Wie |
|---|---|
| Importieren | Button „Importieren“, Strg+I, oder Dateien aus dem Explorer auf die Seite ziehen (Video, Audio, Bilder) |
| Auf Timeline legen | Aus dem Medienpool auf V1/V2 ziehen, Doppelklick (hängt an V1 an), „Auf V1“ / „Auf V2“ (V2 am Abspielkopf) |
| Verschieben | Clip ziehen, auch zwischen V1 und V2; rastet an Clipkanten, 0 und Abspielkopf ein |
| Trimmen | Linke/rechte Clipkante ziehen (begrenzt durch Quelllänge und Nachbarclips) |
| Teilen | S oder „Teilen“: ausgewählter Clip am Abspielkopf, ohne Auswahl alle Spuren |
| Entfernen | Entf (Lücke bleibt), Umschalt+Entf (Lücke schließt sich) |
| Teil ausschneiden | I / O setzen, dann X oder „Bereich ausschneiden“: schneidet auf beiden Spuren und schließt die Lücke |
| Stumm | M schaltet den Ton des ausgewählten Clips stumm (Wellenform wird blass) |
| Medium entfernen | Papierkorb im Medienpool oder Rechtsklick. Clips auf der Timeline werden nach Rückfrage mit entfernt, der Rückgängig-Verlauf wird bereinigt |
| Wiedergabe | Leertaste, ← / → ein Bild, Pos1 / Ende |
| Rückgängig | Strg+Z, Strg+Y bzw. Strg+Umschalt+Z (200 Schritte) |
| Zoom | Strg+Mausrad, Zoom-Regler, „Ganze Timeline zeigen“ |
| Export | Strg+E oder „Exportieren“ |

## Aufbau

- `Core/Editing/Timeline.cs`: Schnittmodell ohne UI, unveränderliche Clips, Undo/Redo über Schnappschüsse. Clips einer Spur überlappen sich nie.
- `Core/Editing/Geometry.cs`: `AspectFit` = größtes unverzerrtes Rechteck, zentriert. Wird für Vorschaurahmen und V2-Overlay benutzt.
- `App/Services/Editing/CompositionBuilder.cs`: Timeline → `Windows.Media.Editing.MediaComposition` (Media Foundation, dieselbe Basis wie die Aufnahme mit MediaCapture).
  - Grundbild: schwarzes PNG in exakt der Ausgabegröße über die ganze Länge (`Core/Editing/SolidPng.cs`). Damit ist der Koordinatenraum der Komposition immer die Ausgabegröße.
  - V1 und V2 → je eine `MediaOverlayLayer` (V2 darüber), jedes Overlay per AspectFit seitenrichtig und größtmöglich
  - reine Audiodateien → `BackgroundAudioTracks`
- `PreviewController`: erzeugt nach Änderungen (250 ms entprellt) einen neuen Vorschau-Stream, behält Position und Wiedergabe bei. Vorschau max. 1280 px Kantenlänge.
- `ExportService`: `RenderToFileAsync` mit Profil aus `EncodingProfileFactory`. Er schreibt erst in `name.exporting.mp4` und benennt die Datei erst nach Erfolg um. Ein Abbruch hinterlässt also keine kaputte Zieldatei.
- `Controls/TimelineControl.cs`: gezeichnete 2-Spur-Timeline (V2 oben, V1 unten), Wellenform der Tonspur im unteren Teil jedes Clips.
- `WaveformService` + `Core/Editing/Waveform.cs`: Hüllkurve (100 Werte/s) im Hintergrund nach dem Import.
- `Views/ExportDialog.cs`: Vorgaben, Container (MP4/WMV), Videocodec (H.264/HEVC/WMV9), Auflösung, Bildrate, Videobitrate, Audio (AAC/WMA, Bitrate, Abtastrate), Größenschätzung, Prüfung ungültiger Kombinationen.

## Projektformat

Oben rechts wählbar (1080p/720p/4K, 9:16, 1:1, 4:3, verschiedene Bildraten). Beim ersten Video auf einer leeren Timeline wird ein passendes Format automatisch gewählt. Vorschau und Export haben immer dieses Seitenverhältnis. Clips mit anderem Format werden unverzerrt mit schwarzen Rändern eingepasst.

## Annahmen, die auf Windows geprüft werden müssen

Hier gibt es kein Windows. Der C#-Code kompiliert ohne Warnungen (geprüft mit Platzhaltern für den XAML-Code), die XAML-Dateien und das Laufzeitverhalten sind aber ungetestet.

1. **Seitenverhältnis (behoben nach Test)**: V2 war verzerrt, weil die Overlay-Positionen im Raum des ersten V1-Clips ausgewertet wurden. Jetzt liegen beide Spuren als Overlays auf einem Grundbild in Ausgabegröße. Bitte erneut prüfen, auch mit V1-Clips in anderem Format und mit Lücken.
2. **Wellenform**: Der Ton wird per `MediaTranscoder` schneller als Echtzeit in ein 8-kHz-Mono-WAV im Temp-Ordner dekodiert. Bei Videodateien muss der Transcoder die Videospur dafür verwerfen, das ist auf Windows zu prüfen.
3. **Gedrehte Handyvideos** (Rotate90/270): die Größe wird getauscht, ob `MediaClip` die Drehung beim Rendern anwendet, ist ungeprüft.
4. **FLAC/PCM im MP4**: im Export bewusst nicht angeboten. Die Aufnahmeseite prüft das zur Laufzeit.
5. **HEVC** braucht die HEVC-Erweiterung aus dem Microsoft Store. Fehlt sie, meldet der Export „Codec nicht verfügbar“.

## Bewusst noch nicht enthalten

Projekt speichern/laden, Überblendungen, Lautstärkeregelung pro Clip (nur Stumm), Thumbnails in der Timeline, freie Position/Größe für V2 (Bild-im-Bild).
