<#
.SYNOPSIS
    Baut GDT2DICOM.msi.

.DESCRIPTION
    Veröffentlicht die Anwendung, erzeugt daraus die Dateiliste für WiX und baut das Paket.

    Die Dateiliste wird generiert statt gepflegt: Der Veröffentlichungsordner enthält über
    200 Dateien, und eine von Hand geführte Liste wäre nach dem ersten Paketwechsel falsch –
    mit dem unangenehmen Fehlerbild, dass eine fehlende Datei erst beim Start auf dem
    Zielrechner auffällt.

.PARAMETER FrameworkDependent
    Kleineres Paket, setzt aber die .NET-10-Desktop-Runtime auf dem Zielrechner voraus.
    Standard ist self-contained: auf einem Praxisrechner ist selten eine Runtime vorhanden.

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -FrameworkDependent
#>
param(
    [switch]$FrameworkDependent,
    [string]$Configuration = 'Release',
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$root = Split-Path $installerDir -Parent
$dist = Join-Path $root 'dist'
$ausgabe = Join-Path $installerDir 'out'

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "WiX fehlt. Einmalig einrichten mit:`n  dotnet tool install --global wix --version 6.0.2`n  wix extension add -g WixToolset.UI.wixext WixToolset.Util.wixext WixToolset.Firewall.wixext"
}

# --- 1. Anwendung veröffentlichen -------------------------------------------------
Write-Host "1/4  Anwendung veröffentlichen ..." -ForegroundColor Cyan
$publishArgs = @()
if ($FrameworkDependent) { $publishArgs += '-FrameworkDependent' }
& (Join-Path $root 'publish.ps1') @publishArgs -Configuration $Configuration | Out-Null

if (-not (Test-Path (Join-Path $dist 'GDT2DICOM.Service.exe'))) {
    throw "Im Veröffentlichungsordner fehlt GDT2DICOM.Service.exe."
}

# --- 2. Version bestimmen ----------------------------------------------------------
if (-not $Version) {
    $exe = Get-Item (Join-Path $dist 'GDT2DICOM.Service.exe')
    $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe.FullName)
    $Version = '{0}.{1}.{2}' -f $vi.FileMajorPart, $vi.FileMinorPart, $vi.FileBuildPart
}
Write-Host "2/4  Version $Version"

# --- 3. Dateiliste erzeugen --------------------------------------------------------
Write-Host "3/4  Dateiliste erzeugen ..." -ForegroundColor Cyan

# GDT2DICOM.Service.exe wird ausgelassen: Sie steht in Package.wxs als Schlüsselpfad der
# Dienstkomponente. Windows Installer nimmt genau diesen Schlüsselpfad als Programmdatei
# des Dienstes - liegt die Exe in einer anderen Komponente, wird der Dienst nicht angelegt.
$dienstExeName = 'GDT2DICOM.Service.exe'
$dienstExePfad = Join-Path $dist $dienstExeName

$dateien = Get-ChildItem $dist -Recurse -File |
           Where-Object { $_.FullName -ne $dienstExePfad } |
           Sort-Object FullName
if ($dateien.Count -eq 0) { throw "Der Veröffentlichungsordner ist leer." }

# Verzeichnisse unterhalb von dist auf WiX-Directory-Elemente abbilden
$verzeichnisse = @{ '' = 'INSTALLFOLDER' }
$nr = 0
function Get-VerzeichnisId([string]$relativ) {
    if ($verzeichnisse.ContainsKey($relativ)) { return $verzeichnisse[$relativ] }
    $script:nr++
    $id = 'dir{0}' -f $script:nr
    $verzeichnisse[$relativ] = $id
    return $id
}

# Zuerst alle Verzeichnisse sammeln, damit die Schachtelung stimmt
$alleOrdner = $dateien | ForEach-Object { $_.DirectoryName.Substring($dist.Length).TrimStart('\') } |
              Select-Object -Unique | Where-Object { $_ } | Sort-Object

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void]$sb.AppendLine('<!-- Erzeugt von build-installer.ps1. Nicht von Hand bearbeiten. -->')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')

# Verzeichnisbaum
[void]$sb.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
$offen = New-Object System.Collections.Generic.Stack[string]
$vorher = ''
foreach ($ordner in $alleOrdner) {
    # Ebenen schließen, die nicht mehr Präfix sind
    while ($offen.Count -gt 0 -and -not $ordner.StartsWith(($offen.ToArray() -join '\') , 'OrdinalIgnoreCase')) {
        [void]$sb.AppendLine(('      ' + ('  ' * $offen.Count) + '</Directory>'))
        [void]$offen.Pop()
    }
    $teile = $ordner.Split('\')
    $name = $teile[-1]
    $id = Get-VerzeichnisId $ordner
    [void]$sb.AppendLine(('      ' + ('  ' * $offen.Count) + "<Directory Id=`"$id`" Name=`"$name`">"))
    [void]$offen.Push($name)
}
while ($offen.Count -gt 0) {
    [void]$offen.Pop()
    [void]$sb.AppendLine(('      ' + ('  ' * $offen.Count) + '</Directory>'))
}
[void]$sb.AppendLine('    </DirectoryRef>')
[void]$sb.AppendLine('')

# Komponenten: eine Datei je Komponente, so verlangt es die MSI-Regel für saubere Updates
[void]$sb.AppendLine('    <ComponentGroup Id="ProgrammDateien">')
$i = 0
foreach ($datei in $dateien) {
    $i++
    $relOrdner = $datei.DirectoryName.Substring($dist.Length).TrimStart('\')
    $dirId = if ($relOrdner) { $verzeichnisse[$relOrdner] } else { 'INSTALLFOLDER' }
    $quelle = $datei.FullName.Replace('&', '&amp;')
    [void]$sb.AppendLine("      <Component Directory=`"$dirId`" Id=`"cmp$i`" Guid=`"*`">")
    [void]$sb.AppendLine("        <File Id=`"fil$i`" Source=`"$quelle`" KeyPath=`"yes`" />")
    [void]$sb.AppendLine('      </Component>')
}
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

$dateiListe = Join-Path $installerDir 'Files.wxs'
[System.IO.File]::WriteAllText($dateiListe, $sb.ToString(), (New-Object System.Text.UTF8Encoding $false))
Write-Host "     $($dateien.Count) Dateien in $($alleOrdner.Count + 1) Verzeichnissen"

# --- 4. Paket bauen ----------------------------------------------------------------
Write-Host "4/4  Paket bauen ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force $ausgabe | Out-Null
$msi = Join-Path $ausgabe 'GDT2DICOM.msi'

& wix build `
    (Join-Path $installerDir 'Package.wxs') `
    $dateiListe `
    -arch x64 `
    -d ProductVersion="$Version" `
    -d IconFile="$(Join-Path $root 'assets\gdt2dicom.ico')" `
    -d ServiceExe="$dienstExePfad" `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -ext WixToolset.Firewall.wixext `
    -o $msi

if ($LASTEXITCODE -ne 0) { throw 'Der Paketbau ist fehlgeschlagen.' }

$groesse = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host ""
Write-Host "Fertig: $msi ($groesse MB)" -ForegroundColor Green
Write-Host ""
Write-Host "Installation:"
Write-Host "  msiexec /i `"$msi`"                              mit Oberfläche"
Write-Host "  msiexec /i `"$msi`" /qn                          unbeaufsichtigt"
Write-Host "  msiexec /i `"$msi`" /qn FIREWALL=1 DICOMPORT=104 zusätzlich Firewallregel"
Write-Host "  msiexec /x `"$msi`" /qn                          entfernen"
Write-Host ""
Write-Host "Konfiguration, Protokolle und DICOM-Archiv unter C:\ProgramData\GDT2DICOM"
Write-Host "bleiben beim Entfernen erhalten."
