# Veröffentlichen (MSIX für andere Rechner)

Das Profil `src/MediaRecorder.App/Properties/PublishProfiles/MSIX-win-x64.pubxml` erzeugt ein **Sideload-MSIX für x64**,
**self-contained**: .NET 8 und das Windows App SDK sind im Paket enthalten, auf dem Zielrechner muss nichts installiert sein.

## Paket bauen (ein Klick)

1. `Build-Msix.cmd` in der Repo-Wurzel doppelklicken.
2. Beim ersten Mal erzeugt das Skript ein Signaturzertifikat unter `certs\` (siehe unten).
3. Ergebnis: `AppPackages\MediaRecorder.App_<Version>_x64_Test\` mit `.msix`, `.cer` und `Install-MediaRecorder.ps1`.
   Der Ordner öffnet sich am Ende im Explorer.

Das Skript nutzt MSBuild aus Visual Studio (`tools\Build-Msix.ps1`). Ohne Skript:

```
msbuild src\MediaRecorder.App\MediaRecorder.App.csproj /restore /p:Configuration=Release /p:Platform=x64 /p:PublishProfile=MSIX-win-x64
```

## Zertifikat

Windows installiert nur signierte MSIX-Pakete, deren Zertifikat der Rechner vertraut.

- `tools\New-SigningCertificate.ps1` erzeugt ein selbstsigniertes Zertifikat mit dem Namen `CN=MediaRecorder Dev`.
  Er muss exakt dem `Publisher` in `Package.appxmanifest` entsprechen.
- `certs\MediaRecorder.pfx` ist der private Schlüssel zum Signieren. **Nicht weitergeben**, er liegt bewusst nicht im Git
  (`.gitignore`). Das Passwort `MediaRecorder` steht im Profil; es schützt nur dieses Entwicklerzertifikat.
- `certs\MediaRecorder.cer` ist der öffentliche Teil und kommt auf die Zielrechner.
- Wer das Zertifikat verliert, erzeugt mit `-Force` ein neues. Zielrechner brauchen dann einmal die neue `.cer`.
- Für eine Verteilung an Fremde besser ein gekauftes Code-Signing-Zertifikat verwenden und `Publisher` sowie
  `PackageCertificateKeyFile`/`PackageCertificatePassword` im Profil anpassen.

## Auf einem anderen Rechner installieren

1. Den Ordner `MediaRecorder.App_<Version>_x64_Test` auf den Zielrechner kopieren.
2. `Install-MediaRecorder.ps1` per Rechtsklick > „Mit PowerShell ausführen“ **als Administrator** starten.
   Es importiert die `.cer` nach „Lokaler Computer > Vertrauenswürdige Personen“ (nur beim ersten Mal nötig)
   und installiert das Paket.
3. Alternativ von Hand: `.cer` doppelklicken > Zertifikat installieren > Lokaler Computer >
   „Vertrauenswürdige Personen“, danach die `.msix` doppelklicken.

Der Entwicklermodus ist auf dem Zielrechner **nicht** nötig. Updates: neues Paket genauso installieren,
Einstellungen bleiben erhalten.

## Version

Die Paketversion (`Identity Version` in `Package.appxmanifest`) folgt der Subversion aus `Version.props`
(1.52 → 1.52.0.0). Windows aktualisiert ein installiertes Paket nur mit einer höheren Version.
Der Build warnt, wenn beide nicht zusammenpassen.
