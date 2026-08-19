<#
.SYNOPSIS
    Erzeugt aus logo.png die Fassung, die die Oberfläche einbettet:
    verkleinert und mit freigestelltem Hintergrund.

.DESCRIPTION
    Die Vorlage hat einen nahezu weißen, leicht verlaufenden Hintergrund. Auf der weißen
    Oberfläche zeichnet sich der dadurch als blasser Kasten ab. Freigestellt wird nur, was
    tatsächlich Hintergrund ist: nahezu unbunte und sehr helle Pixel. Farbige Flächen und
    der dunkle Schriftzug bleiben unberührt, die weichen Kanten dazwischen bekommen einen
    gleitenden Übergang – sonst bekäme die Schrift ausgefranste Ränder.

.EXAMPLE
    .\prepare-logo.ps1
#>
param(
    [string]$Quelle = (Join-Path (Split-Path $PSScriptRoot -Parent) 'logo.png'),
    [string]$Ziel   = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\GDT2DICOM.Gui\Assets\pliete-logo.png'),
    [int]$Breite = 420
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Quelle)) { throw "Vorlage nicht gefunden: $Quelle" }

$original = [System.Drawing.Bitmap]::FromFile($Quelle)
$hoehe = [int]($original.Height * ($Breite / $original.Width))

# Verkleinern
$klein = New-Object System.Drawing.Bitmap($Breite, $hoehe, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($klein)
$g.InterpolationMode = 'HighQualityBicubic'
$g.SmoothingMode = 'HighQuality'
$g.PixelOffsetMode = 'HighQuality'
$g.DrawImage($original, 0, 0, $Breite, $hoehe)
$g.Dispose()
$original.Dispose()

# Freistellen
$untenVoll = 225   # ab hier voll deckend
$obenLeer  = 250   # ab hier vollständig transparent
$maxBuntheit = 14  # größerer Abstand zwischen den Kanälen heißt: farbig, also Motiv

$freigestellt = New-Object System.Drawing.Bitmap($Breite, $hoehe, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$entfernt = 0

for ($y = 0; $y -lt $hoehe; $y++) {
    for ($x = 0; $x -lt $Breite; $x++) {
        $c = $klein.GetPixel($x, $y)
        $max = [Math]::Max($c.R, [Math]::Max($c.G, $c.B))
        $min = [Math]::Min($c.R, [Math]::Min($c.G, $c.B))
        $helligkeit = ($c.R + $c.G + $c.B) / 3

        $alpha = 255
        if (($max - $min) -le $maxBuntheit -and $helligkeit -gt $untenVoll) {
            if ($helligkeit -ge $obenLeer) {
                $alpha = 0
            } else {
                $alpha = [int](255 * ($obenLeer - $helligkeit) / ($obenLeer - $untenVoll))
            }
        }

        if ($alpha -lt 255) { $entfernt++ }
        $freigestellt.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B))
    }
}

$klein.Dispose()
New-Item -ItemType Directory -Force (Split-Path $Ziel) | Out-Null
$freigestellt.Save($Ziel, [System.Drawing.Imaging.ImageFormat]::Png)
$freigestellt.Dispose()

$anteil = [math]::Round(100 * $entfernt / ($Breite * $hoehe))
Write-Host "Logo erzeugt: $Ziel"
Write-Host ("  {0}x{1}, {2} KB, {3} % der Fläche freigestellt" -f `
    $Breite, $hoehe, [math]::Round((Get-Item $Ziel).Length / 1kb), $anteil)
