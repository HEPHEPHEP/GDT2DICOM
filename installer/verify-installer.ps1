<#
.SYNOPSIS
    Prüft GDT2DICOM.msi durch eine echte Installation. Benötigt Administratorrechte.

.DESCRIPTION
    Spielt den vollständigen Lebenszyklus durch: installieren, den laufenden Dienst prüfen,
    wieder entfernen und nachweisen, dass die Daten unter C:\ProgramData\GDT2DICOM die
    Deinstallation überstanden haben.

    Letzteres ist der wichtigste Punkt: Dort liegen Konfiguration, Protokolle, Worklist und
    das DICOM-Archiv. Ein Installationspaket, das die bei der Deinstallation mitnimmt, wäre
    ein Datenverlust mit Ansage – deshalb wird vorher eine Markierungsdatei abgelegt und
    hinterher ihr Verbleib geprüft.

.PARAMETER Behalten
    Am Ende installiert lassen, statt wieder zu entfernen.

.EXAMPLE
    .\verify-installer.ps1
    .\verify-installer.ps1 -Behalten
#>
param(
    [switch]$Behalten,
    [string]$Msi = (Join-Path $PSScriptRoot 'out\GDT2DICOM.msi'),
    [string]$Bericht = ''
)

$ErrorActionPreference = 'Continue'

if ($Bericht) { Start-Transcript -Path $Bericht -Force | Out-Null }

$erhoeht = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $erhoeht) {
    Write-Host "Dieses Skript braucht Administratorrechte." -ForegroundColor Red
    Write-Host "Bitte PowerShell als Administrator starten und erneut ausführen."
    if ($Bericht) { Stop-Transcript | Out-Null }
    exit 1
}

if (-not (Test-Path $Msi)) {
    Write-Host "Paket nicht gefunden: $Msi" -ForegroundColor Red
    Write-Host "Zuerst .\build-installer.ps1 ausführen."
    if ($Bericht) { Stop-Transcript | Out-Null }
    exit 1
}

$programm  = 'C:\Program Files\GDT2DICOM'
$daten     = 'C:\ProgramData\GDT2DICOM'
$markierung = Join-Path $daten 'darf-nicht-geloescht-werden.txt'
$verknuepfung = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\GDT2DICOM\GDT2DICOM Konfiguration.lnk'
$protokoll = Join-Path $env:TEMP 'gdt2dicom-msi.log'

$e = [ordered]@{}
function Pruefe($name, $wert) { $script:e[$name] = [bool]$wert; return [bool]$wert }

Write-Host "================ AUSGANGSLAGE ================" -ForegroundColor Cyan
Write-Host "Paket:        $Msi ($([math]::Round((Get-Item $Msi).Length/1MB,1)) MB)"
Write-Host "Dienst da:    $($null -ne (Get-Service GDT2DICOM -ErrorAction SilentlyContinue))"
Write-Host "Programm da:  $(Test-Path $programm)"
Write-Host "Daten da:     $(Test-Path $daten)"

# Markierung setzen: sie muss die Deinstallation überleben
New-Item -ItemType Directory -Force $daten | Out-Null
"Diese Datei muss eine Deinstallation überstehen. Erzeugt $(Get-Date -Format s)." |
    Set-Content $markierung -Encoding UTF8
$datenVorher = @(Get-ChildItem $daten -Recurse -File -ErrorAction SilentlyContinue).Count
Write-Host "Dateien unter ProgramData vorher: $datenVorher (inklusive Markierung)"

# ================= INSTALLATION =================
Write-Host "`n================ INSTALLATION ================" -ForegroundColor Cyan
$p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$Msi`"", '/qn', '/l*v', "`"$protokoll`"") -Wait -PassThru
Write-Host "msiexec Rückgabewert: $($p.ExitCode)  (0 = erfolgreich, 3010 = Neustart empfohlen)"
Pruefe 'Installation erfolgreich' ($p.ExitCode -in 0, 3010) | Out-Null

Start-Sleep -Seconds 3
$dienst = Get-Service -Name 'GDT2DICOM' -ErrorAction SilentlyContinue
Write-Host "`nDienst:"
if ($dienst) {
    $wmi = Get-CimInstance Win32_Service -Filter "Name='GDT2DICOM'"
    Write-Host "   Anzeigename : $($dienst.DisplayName)"
    Write-Host "   Zustand     : $($dienst.Status)"
    Write-Host "   Starttyp    : $($wmi.StartMode)"
    Write-Host "   Konto       : $($wmi.StartName)"
    Write-Host "   Pfad        : $($wmi.PathName)"
    Pruefe 'Dienst registriert'        $true | Out-Null
    Pruefe 'Dienst läuft'             ($dienst.Status -eq 'Running') | Out-Null
    Pruefe 'Start automatisch'         ($wmi.StartMode -eq 'Auto') | Out-Null
    Pruefe 'Pfad in Program Files'     ($wmi.PathName -like "*$programm*") | Out-Null
} else {
    Write-Host "   NICHT VORHANDEN" -ForegroundColor Red
    Pruefe 'Dienst registriert' $false | Out-Null
}

Write-Host "`nNeustartverhalten nach Absturz:"
$failure = (& sc.exe qfailure GDT2DICOM) -join "`n"
$failure -split "`n" | Where-Object { $_ -match 'RESET_PERIOD|RESTART|FAILURE_ACTIONS|NEUSTART' } | ForEach-Object { Write-Host "   $($_.Trim())" }
Pruefe 'Neustart nach Absturz gesetzt' ($failure -match 'RESTART|Neustart') | Out-Null

Write-Host "`nDateien:"
$installiert = @(Get-ChildItem $programm -Recurse -File -ErrorAction SilentlyContinue)
Write-Host "   Dateien in Program Files: $($installiert.Count)"
foreach ($n in 'GDT2DICOM.Service.exe','GDT2DICOM.Konfiguration.exe','GDT2DICOM.Aufruf.exe') {
    $da = Test-Path (Join-Path $programm $n)
    Write-Host ("   {0,-32} {1}" -f $n, $(if ($da) { 'da' } else { 'FEHLT' }))
    Pruefe "Datei $n" $da | Out-Null
}
Pruefe 'Startmenü-Verknüpfung' (Test-Path $verknuepfung) | Out-Null
Write-Host "   Startmenü-Verknüpfung: $(Test-Path $verknuepfung)"

Write-Host "`nEintrag in Programme und Features:"
$arp = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
       ForEach-Object { Get-ItemProperty $_.PSPath } |
       Where-Object { $_.DisplayName -eq 'GDT2DICOM' }
if ($arp) {
    Write-Host "   $($arp.DisplayName) $($arp.DisplayVersion) von $($arp.Publisher)"
    Write-Host "   Hilfe: $($arp.HelpLink)"
}
Pruefe 'Eintrag in Programme und Features' ($null -ne $arp) | Out-Null

# ================= DIENST WIRKLICH FUNKTIONSFAEHIG? =================
Write-Host "`n================ LAEUFT ER AUCH? ================" -ForegroundColor Cyan
Start-Sleep -Seconds 4

$pipeOk = $false
try {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'GDT2DICOM.Control', 'InOut')
    $pipe.Connect(8000)
    $w = New-Object System.IO.StreamWriter($pipe); $w.AutoFlush = $true
    $r = New-Object System.IO.StreamReader($pipe)
    $w.WriteLine('{"Command":"status.get","Payload":""}')
    $antwort = $r.ReadLine()
    $pipe.Dispose()
    $status = ($antwort | ConvertFrom-Json).Payload | ConvertFrom-Json
    Write-Host "   Steuerkanal antwortet."
    Write-Host "   DICOM-Server: $(if ($status.DicomServerRunning) { "läuft als $($status.DicomAeTitle) auf Port $($status.DicomPort)" } else { "nicht aktiv: $($status.DicomServerError)" })"
    Write-Host "   GDT-Überwachung: $(if ($status.GdtWatcherRunning) { "läuft auf $($status.GdtInboxDirectory)" } else { 'nicht aktiv' })"
    $pipeOk = $true
} catch {
    Write-Host "   Steuerkanal nicht erreichbar: $($_.Exception.Message)" -ForegroundColor Red
}
Pruefe 'Dienst antwortet auf dem Steuerkanal' $pipeOk | Out-Null

$log = Get-ChildItem 'C:\ProgramData\GDT2DICOM\logs' -Filter '*.log' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
    Write-Host "`n   Letzte Protokollzeilen:"
    Get-Content $log.FullName -Tail 6 | ForEach-Object { Write-Host "      $_" }
}

# ================= DEINSTALLATION =================
if ($Behalten) {
    Write-Host "`n================ BLEIBT INSTALLIERT ================" -ForegroundColor Yellow
    Write-Host "Entfernen später mit:  msiexec /x `"$Msi`" /qn"
} else {
    Write-Host "`n================ DEINSTALLATION ================" -ForegroundColor Cyan
    $p = Start-Process msiexec.exe -ArgumentList @('/x', "`"$Msi`"", '/qn', '/l*v', "`"$protokoll.deinstall`"") -Wait -PassThru
    Write-Host "msiexec Rückgabewert: $($p.ExitCode)"
    Pruefe 'Deinstallation erfolgreich' ($p.ExitCode -in 0, 3010) | Out-Null

    Start-Sleep -Seconds 3
    $dienstDanach = Get-Service -Name 'GDT2DICOM' -ErrorAction SilentlyContinue
    Write-Host "   Dienst noch vorhanden:      $($null -ne $dienstDanach)"
    Write-Host "   Programmordner noch da:     $(Test-Path $programm)"
    Write-Host "   Verknüpfung noch da:       $(Test-Path $verknuepfung)"
    Pruefe 'Dienst entfernt'        ($null -eq $dienstDanach) | Out-Null
    Pruefe 'Programmordner entfernt' (-not (Test-Path $programm)) | Out-Null
    Pruefe 'Verknüpfung entfernt'   (-not (Test-Path $verknuepfung)) | Out-Null

    Write-Host "`n   --- Der entscheidende Punkt: Behandlungsdaten ---"
    $markierungDa = Test-Path $markierung
    $datenNachher = @(Get-ChildItem $daten -Recurse -File -ErrorAction SilentlyContinue).Count
    Write-Host "   ProgramData noch vorhanden: $(Test-Path $daten)"
    Write-Host "   Markierungsdatei noch da:   $markierungDa"
    Write-Host "   Dateien vorher/nachher:     $datenVorher / $datenNachher"
    Pruefe 'ProgramData überlebt'      (Test-Path $daten) | Out-Null
    Pruefe 'Markierungsdatei überlebt' $markierungDa | Out-Null
    Pruefe 'Keine Datei verloren'       ($datenNachher -ge $datenVorher) | Out-Null
}

Write-Host "`n================ ZUSAMMENFASSUNG ================" -ForegroundColor Cyan
$alle = $true
foreach ($k in $e.Keys) {
    $ok = $e[$k]
    Write-Host ("  {0,-42} {1}" -f $k, $(if ($ok) { 'OK' } else { 'FEHLER' })) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    if (-not $ok) { $alle = $false }
}
Write-Host ""
Write-Host "=== INSTALLER-PRUEFUNG: $(if ($alle) { 'BESTANDEN' } else { 'FEHLGESCHLAGEN' }) ===" -ForegroundColor $(if ($alle) { 'Green' } else { 'Red' })
Write-Host ""
Write-Host "Ausführliches msiexec-Protokoll: $protokoll"

Remove-Item $markierung -Force -ErrorAction SilentlyContinue

if ($Bericht) { Stop-Transcript | Out-Null }
exit $(if ($alle) { 0 } else { 1 })
