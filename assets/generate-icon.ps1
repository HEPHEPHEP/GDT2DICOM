<#
.SYNOPSIS
    Erzeugt assets\gdt2dicom.ico (und eine PNG-Vorschau) aus einer in Code beschriebenen
    Zeichnung. Das Icon wird damit reproduzierbar und lässt sich ohne Grafikprogramm ändern.

.DESCRIPTION
    Motiv: der Sektor eines Ultraschallschallkopfs, durchbrochen von einem Doppelpfeil –
    Sonographie plus Datenaustausch in beide Richtungen.

    Jede Größe wird einzeln gezeichnet statt aus einer großen Fassung herunterskaliert;
    das hält die kleinen Symbolgrößen scharf. Unter 24 Pixeln entfällt der Doppelpfeil,
    weil er dort nur noch Matsch wäre.

.EXAMPLE
    .\generate-icon.ps1
#>
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'gdt2dicom.ico'),
    [string]$PreviewPath = (Join-Path $PSScriptRoot 'gdt2dicom-preview.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# .NET 10 hat die GDI+-Typen auf mehrere Assemblies verteilt; alle drei werden gebraucht.
# Eine ausdrückliche Referenzliste ersetzt die Vorgaben von Add-Type, deshalb müssen
# auch die Basis-Assemblies mit aufgeführt werden.
$gdiRefs = @(
    'netstandard'
    'System.Runtime'
    'System.Collections'
    'System.Runtime.InteropServices'
    'System.Drawing.Common'
    'System.Drawing.Primitives'
    'System.Private.Windows.GdiPlus'
    'System.Private.Windows.Core'
)

Add-Type -ReferencedAssemblies $gdiRefs @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class IconFactory
{
    // Entwurfsraster: 256 x 256. Alle Maße sind darauf bezogen und werden skaliert.
    const float Design = 256f;

    static readonly Color BackTop    = Color.FromArgb(255, 0x2A, 0x86, 0xC4);
    static readonly Color BackBottom = Color.FromArgb(255, 0x0E, 0x40, 0x66);
    static readonly Color Ink        = Color.FromArgb(255, 0xFF, 0xFF, 0xFF);

    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        // Unter 24 Pixeln ist für Schallkopf und Pfeil kein Platz mehr. Dort wird ein
        // vereinfachtes Motiv gezeichnet: ein einzelner, kräftiger Sektor.
        var small = size < 24;

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            var scale = size / Design;
            g.ScaleTransform(scale, scale);

            var inset = small ? 3f : 10f;
            var radius = small ? 28f : 52f;

            using (var badge = RoundedRect(inset, inset, Design - 2 * inset, Design - 2 * inset, radius))
            {
                // Die Verlaufsachse muss länger sein als die Projektion des Quadrats
                // darauf, sonst kachelt GDI+ den Verlauf und es entsteht eine sichtbare
                // Kante in der gegenüberliegenden Ecke.
                using (var brush = new LinearGradientBrush(
                           new PointF(0, 0), new PointF(Design * 0.5f, Design * 1.3f), BackTop, BackBottom))
                {
                    brush.WrapMode = WrapMode.TileFlipXY;
                    g.FillPath(brush, badge);
                }

                // Alles Weitere bleibt innerhalb des Trägers, damit nichts über die
                // abgerundeten Ecken hinausläuft.
                g.SetClip(badge);

                using (var ink = new SolidBrush(Ink))
                {
                    if (!small)
                    {
                        using (var transducer = RoundedRect(102f, 45f, 52f, 26f, 12f))
                            g.FillPath(ink, transducer);
                    }

                    DrawFan(g, ink, small);
                }

                g.ResetClip();
            }
        }

        return bmp;
    }

    // Der Schallsektor. In der großen Fassung wird der Doppelpfeil ausgespart.
    static void DrawFan(Graphics g, Brush ink, bool small)
    {
        // 0 Grad zeigt nach rechts, gezählt wird im Uhrzeigersinn; 90 Grad ist unten.
        // Das Motiv sitzt bewusst nicht randfüllend: ein Symbol, das den Träger ausfüllt,
        // wirkt in der Taskleiste wie ein weißer Fleck.
        var apexY = small ? 60f : 71f;
        var inner = small ? 0f : 14f;
        var outer = small ? 150f : 140f;
        var start = small ? 50f : 53f;
        var sweep = small ? 80f : 74f;

        using (var fan = new GraphicsPath())
        {
            fan.AddArc(128f - outer, apexY - outer, outer * 2, outer * 2, start, sweep);
            if (inner > 0)
                fan.AddArc(128f - inner, apexY - inner, inner * 2, inner * 2, start + sweep, -sweep);
            else
                fan.AddLine(128f, apexY, 128f, apexY);
            fan.CloseFigure();

            if (small)
            {
                g.FillPath(ink, fan);
                return;
            }

            using (var region = new Region(fan))
            using (var arrow = DoubleArrow())
            {
                region.Exclude(arrow);
                g.FillRegion(ink, region);
            }
        }
    }

    /// <summary>
    /// Waagerechter Doppelpfeil als Aussparung. Er bleibt bewusst vollständig innerhalb
    /// des Sektors – durchstößt er dessen Kanten, zerfällt die Silhouette.
    /// </summary>
    static GraphicsPath DoubleArrow()
    {
        const float y = 162f;          // Mittellinie
        const float bar = 18f;         // Balkenstärke
        const float headW = 26f;       // Länge einer Pfeilspitze
        const float headH = 40f;       // Höhe einer Pfeilspitze
        const float left = 74f, right = 182f;

        var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(left, y),
            new PointF(left + headW, y - headH / 2),
            new PointF(left + headW, y - bar / 2),
            new PointF(right - headW, y - bar / 2),
            new PointF(right - headW, y - headH / 2),
            new PointF(right, y),
            new PointF(right - headW, y + headH / 2),
            new PointF(right - headW, y + bar / 2),
            new PointF(left + headW, y + bar / 2),
            new PointF(left + headW, y + headH / 2)
        });
        path.CloseFigure();
        return path;
    }

    static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var d = r * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // -------------------------------------------------------------------
    // ICO-Datei schreiben
    // -------------------------------------------------------------------

    /// <summary>
    /// Schreibt eine Icon-Datei. Größen bis 64 werden als DIB abgelegt (das versteht
    /// jede Windows-Version und jeder Ressourcen-Compiler), 128 und 256 als PNG,
    /// weil unkomprimiert sonst allein 256 KB pro Bild anfallen würden.
    /// </summary>
    public static void WriteIco(string path, int[] sizes)
    {
        var images = new List<byte[]>();
        var isPng = new List<bool>();

        foreach (var size in sizes)
        {
            using (var bmp = Render(size))
            {
                if (size >= 128)
                {
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        images.Add(ms.ToArray());
                        isPng.Add(true);
                    }
                }
                else
                {
                    images.Add(BuildDib(bmp));
                    isPng.Add(false);
                }
            }
        }

        using (var file = File.Create(path))
        using (var w = new BinaryWriter(file))
        {
            w.Write((ushort)0);              // reserviert
            w.Write((ushort)1);              // Typ: Icon
            w.Write((ushort)sizes.Length);

            var offset = 6 + 16 * sizes.Length;
            for (var i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0);            // Farbanzahl (0 = truecolor)
                w.Write((byte)0);            // reserviert
                w.Write((ushort)1);          // Ebenen
                w.Write((ushort)32);         // Bit pro Pixel
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }

            foreach (var image in images)
                w.Write(image);
        }
    }

    /// <summary>BITMAPINFOHEADER + BGRA-Bilddaten (von unten nach oben) + AND-Maske.</summary>
    static byte[] BuildDib(Bitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var maskStride = ((w + 31) / 32) * 4;

        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write(40);                    // biSize
            writer.Write(w);                     // biWidth
            writer.Write(h * 2);                 // biHeight: Bild plus Maske
            writer.Write((ushort)1);             // biPlanes
            writer.Write((ushort)32);            // biBitCount
            writer.Write(0);                     // biCompression = BI_RGB
            writer.Write(w * h * 4 + maskStride * h);
            writer.Write(0); writer.Write(0);    // Auflösung
            writer.Write(0); writer.Write(0);    // Farbtabelle

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[w * 4];
                for (var y = h - 1; y >= 0; y--)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    writer.Write(row);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            // AND-Maske: alles null, die Transparenz steckt bereits im Alphakanal.
            writer.Write(new byte[maskStride * h]);

            return ms.ToArray();
        }
    }

    /// <summary>Übersichtsbild aller Größen nebeneinander, zur Sichtprüfung.</summary>
    public static void WritePreview(string path, int[] sizes)
    {
        const int pad = 16;
        var width = pad;
        foreach (var s in sizes) width += s + pad;
        var height = 256 + 2 * pad + 20;

        using (var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(255, 245, 246, 248));
            var x = pad;
            foreach (var size in sizes)
            {
                using (var bmp = Render(size))
                    g.DrawImageUnscaled(bmp, x, pad + (256 - size));
                x += size + pad;
            }
            sheet.Save(path, ImageFormat.Png);
        }
    }
}
'@

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

[IconFactory]::WriteIco($OutputPath, $sizes)
[IconFactory]::WritePreview($PreviewPath, $sizes)

$kb = [math]::Round((Get-Item $OutputPath).Length / 1kb, 1)
Write-Host "Icon erzeugt: $OutputPath ($kb KB, Größen: $($sizes -join ', '))"
Write-Host "Vorschau:     $PreviewPath"
