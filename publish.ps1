<#
.SYNOPSIS
    Erstellt ein verteilbares Paket von GDT2DICOM unter .\dist.

.PARAMETER FrameworkDependent
    Erzeugt ein deutlich kleineres Paket, setzt aber die installierte
    .NET-10-Desktop-Runtime auf dem Zielrechner voraus. Standard ist
    self-contained, weil auf Praxisrechnern selten eine Runtime vorhanden ist.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -FrameworkDependent
#>
param(
    [switch]$FrameworkDependent,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

$selfContained = -not $FrameworkDependent
$common = @(
    '-c', $Configuration
    '-r', 'win-x64'
    "--self-contained=$($selfContained.ToString().ToLower())"
    '-p:DebugType=none'
)

Write-Host "Veroeffentliche GDT2DICOM ($(if ($selfContained) { 'self-contained' } else { 'framework-dependent' })) ..." -ForegroundColor Cyan

# Dienst und Oberflaeche landen im selben Ordner: die GUI sucht die Dienst-Exe daneben.
dotnet publish (Join-Path $root 'src\GDT2DICOM.Service\GDT2DICOM.Service.csproj') @common -o $dist
if ($LASTEXITCODE -ne 0) { throw 'Publish des Dienstes fehlgeschlagen.' }

dotnet publish (Join-Path $root 'src\GDT2DICOM.Gui\GDT2DICOM.Gui.csproj') @common -o $dist
if ($LASTEXITCODE -ne 0) { throw 'Publish der Oberflaeche fehlgeschlagen.' }

dotnet publish (Join-Path $root 'src\GDT2DICOM.Connector\GDT2DICOM.Connector.csproj') @common -o $dist
if ($LASTEXITCODE -ne 0) { throw 'Publish des Connectors fehlgeschlagen.' }

dotnet publish (Join-Path $root 'tools\GDT2DICOM.TestClient\GDT2DICOM.TestClient.csproj') @common -o (Join-Path $dist 'Testclient')
if ($LASTEXITCODE -ne 0) { throw 'Publish des Testclients fehlgeschlagen.' }

Copy-Item (Join-Path $root 'README.md') $dist -Force -ErrorAction SilentlyContinue

$size = [math]::Round(((Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)

Write-Host ""
Write-Host "Fertig: $dist ($size MB)" -ForegroundColor Green
Write-Host ""
Write-Host "Naechste Schritte auf dem Zielrechner:"
Write-Host "  1. Den Ordner nach C:\Program Files\GDT2DICOM kopieren."
Write-Host "  2. GDT2DICOM.Konfiguration.exe starten."
Write-Host "  3. Auf dem Reiter Status auf 'Installieren' klicken (Adminrechte)."
Write-Host "  4. Verzeichnisse, AE-Titel und Port einstellen, speichern."
Write-Host "  5. Ruft das PVS ein Fremdprogramm auf: unter PVS/GDT die Aufrufzeile kopieren"
Write-Host "     und in der Schnittstellenkonfiguration des PVS eintragen."
