<#
  Installiert Media Recorder auf einem anderen Rechner.
  Diese Datei zusammen mit der .msix und der .cer in einen Ordner legen und als Administrator ausführen:
    powershell -ExecutionPolicy Bypass -File Install-MediaRecorder.ps1

  1. Importiert das Zertifikat nach „Vertrauenswürdige Personen“ (Lokaler Computer), nur einmal nötig.
  2. Installiert das MSIX-Paket für den angemeldeten Benutzer.
#>
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

$cer  = Get-ChildItem -Path $here -Filter *.cer  | Select-Object -First 1
$msix = Get-ChildItem -Path $here -Filter *.msix | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $cer)  { throw "Keine .cer-Datei in $here gefunden." }
if (-not $msix) { throw "Keine .msix-Datei in $here gefunden." }

Import-Certificate -FilePath $cer.FullName -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
Write-Host "Zertifikat importiert: $($cer.Name)"

Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown
Write-Host "Installiert: $($msix.Name). Media Recorder ist jetzt im Startmenü."
