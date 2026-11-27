<#
  Baut das Sideload-MSIX (x64, self-contained) mit dem Profil MSIX-win-x64.
  Aufruf aus beliebigem Ordner (keine Adminrechte nötig), oder Doppelklick auf Build-Msix.cmd:
    powershell -ExecutionPolicy Bypass -File tools\Build-Msix.ps1

  Ergebnis: AppPackages\MediaRecorder.App_<Version>_x64_Test\ mit .msix, .cer und Install-MediaRecorder.ps1.
#>
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "src\MediaRecorder.App\MediaRecorder.App.csproj"

# 1. Signaturzertifikat (einmalig)
if (-not (Test-Path (Join-Path $repo "certs\MediaRecorder.pfx"))) {
    & (Join-Path $PSScriptRoot "New-SigningCertificate.ps1")
}

# 2. MSBuild von Visual Studio suchen (dotnet build kann das MSIX nicht zuverlässig erzeugen)
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe nicht gefunden. Ist Visual Studio installiert?" }
$msbuild = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild.exe nicht gefunden." }
Write-Host "MSBuild: $msbuild"

# 3. Bauen und paketieren
& $msbuild $proj /restore /m /nologo /v:minimal `
    /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 `
    /p:PublishProfile=MSIX-win-x64
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen (Exitcode $LASTEXITCODE)." }

# 4. Installationshilfe neben das Paket legen
$out = Get-ChildItem -Path (Join-Path $repo "AppPackages") -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Copy-Item (Join-Path $PSScriptRoot "Install-MediaRecorder.ps1") $out.FullName -Force
if (-not (Get-ChildItem $out.FullName -Filter *.cer)) {
    Copy-Item (Join-Path $repo "certs\MediaRecorder.cer") $out.FullName -Force
}
Write-Host ""
Write-Host "Fertig: $($out.FullName)"
Write-Host "Diesen Ordner auf den Zielrechner kopieren und dort Install-MediaRecorder.ps1 als Administrator ausführen."
explorer.exe $out.FullName
