<#
  Erzeugt ein selbstsigniertes Zertifikat zum Signieren des MSIX-Pakets (einmalig, auf dem Entwicklungsrechner).

  Ergebnis:
    certs\MediaRecorder.pfx  privater Schlüssel, nur für den Build (NICHT weitergeben, liegt nicht im Git)
    certs\MediaRecorder.cer  öffentliches Zertifikat, wird auf Zielrechnern als vertrauenswürdig importiert

  Aufruf (normale PowerShell, keine Adminrechte nötig):
    powershell -ExecutionPolicy Bypass -File tools\New-SigningCertificate.ps1
#>
param(
    # Muss exakt dem Publisher in src\MediaRecorder.App\Package.appxmanifest entsprechen.
    [string]$Subject = "CN=MediaRecorder Dev",
    # Muss zu PackageCertificatePassword im Veröffentlichungsprofil passen.
    [string]$Password = "MediaRecorder",
    [int]$Years = 5,
    [switch]$Force
)
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$dir  = Join-Path $repo "certs"
$pfx  = Join-Path $dir "MediaRecorder.pfx"
$cer  = Join-Path $dir "MediaRecorder.cer"

if ((Test-Path $pfx) -and -not $Force) {
    Write-Host "Zertifikat existiert bereits: $pfx (mit -Force neu erzeugen)."
    exit 0
}
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $Subject `
    -KeyUsage DigitalSignature `
    -FriendlyName "MediaRecorder Paket-Signatur" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears($Years) `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

$secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
Export-Certificate    -Cert $cert -FilePath $cer | Out-Null

Write-Host "Fertig."
Write-Host "  Signatur (Build): $pfx"
Write-Host "  Für Zielrechner:  $cer"
Write-Host "Fingerabdruck: $($cert.Thumbprint)"
