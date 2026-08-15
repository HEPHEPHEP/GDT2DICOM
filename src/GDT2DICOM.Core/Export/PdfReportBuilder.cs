using Gdt2Dicom.Core.Configuration;
using Microsoft.Extensions.Logging;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Gdt2Dicom.Core.Export;

/// <summary>Angaben, die im Kopf des Befundblatts stehen.</summary>
public sealed record PdfReportHeader(
    string PatientName,
    string PatientId,
    string BirthDate,
    string Sex,
    string StudyDate,
    string StudyTime,
    string AccessionNumber,
    string ProcedureDescription,
    string Modality,
    string DeviceName);

/// <summary>Erzeugt ein Befundblatt als PDF: Kopfdaten, Messwerte aus dem SR, danach die Bilder.</summary>
public sealed class PdfReportBuilder
{
    private const double Margin = 40;

    private readonly ILogger _logger;
    private static bool _fontsInitialized;

    public PdfReportBuilder(ILogger logger) => _logger = logger;

    private static void EnsureFonts()
    {
        if (_fontsInitialized)
            return;

        // PdfSharp 6 greift unter Windows nur nach expliziter Freigabe auf die Systemschriften zu.
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        _fontsInitialized = true;
    }

    public string? Build(string targetPath, PdfReportHeader header, IReadOnlyList<string> imagePaths,
        IReadOnlyList<string> reportLines, ExportConfig config)
    {
        try
        {
            EnsureFonts();

            var pdfA = config.PdfFormat == PdfFormat.PdfA3b;

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var document = new PdfDocument();
            document.Info.Title = $"{config.PdfHeaderTitle} – {header.PatientName}";
            document.Info.Subject = header.ProcedureDescription;
            document.Info.Creator = "GDT2DICOM";

            // PDF/A verlangt vollständig eingebettete Schriften. Unicode-Kodierung erzwingt
            // CID-Schriften, die PdfSharp als Untermenge einbettet.
            var fontOptions = pdfA ? new XPdfFontOptions(PdfFontEncoding.Unicode) : null;

            var titleFont = SafeFont(15, XFontStyleEx.Bold, fontOptions, "Arial", "Segoe UI", "Verdana");
            var labelFont = SafeFont(9, XFontStyleEx.Bold, fontOptions, "Arial", "Segoe UI", "Verdana");
            var textFont = SafeFont(9, XFontStyleEx.Regular, fontOptions, "Arial", "Segoe UI", "Verdana");
            var monoFont = SafeFont(8.5, XFontStyleEx.Regular, fontOptions, "Courier New", "Consolas", "Arial");
            var footerFont = SafeFont(7.5, XFontStyleEx.Regular, fontOptions, "Arial", "Segoe UI", "Verdana");

            var page = NewPage(document, out var gfx);
            var y = Margin;

            // --- Kopf ---
            gfx.DrawString(config.PdfHeaderTitle, titleFont, XBrushes.Black, new XPoint(Margin, y + 12));
            if (!string.IsNullOrWhiteSpace(config.PdfPracticeName))
            {
                gfx.DrawString(config.PdfPracticeName, textFont, XBrushes.Black,
                    new XRect(Margin, y, page.Width.Point - 2 * Margin, 16), XStringFormats.TopRight);
            }
            y += 24;
            gfx.DrawLine(new XPen(XColors.Black, 0.8), Margin, y, page.Width.Point - Margin, y);
            y += 12;

            // --- Patienten- und Untersuchungsdaten in zwei Spalten ---
            var columnWidth = (page.Width.Point - 2 * Margin) / 2;
            var left = new[]
            {
                ("Patient", header.PatientName),
                ("Patienten-Nr.", header.PatientId),
                ("Geburtsdatum", header.BirthDate),
                ("Geschlecht", header.Sex)
            };
            var right = new[]
            {
                ("Untersuchung", header.ProcedureDescription),
                ("Datum", $"{header.StudyDate} {header.StudyTime}".Trim()),
                ("Auftragsnr.", header.AccessionNumber),
                ("Gerät", string.IsNullOrWhiteSpace(header.DeviceName) ? header.Modality : header.DeviceName)
            };

            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                if (i < left.Length)
                    DrawPair(gfx, labelFont, textFont, Margin, y, columnWidth, left[i].Item1, left[i].Item2);
                if (i < right.Length)
                    DrawPair(gfx, labelFont, textFont, Margin + columnWidth, y, columnWidth, right[i].Item1, right[i].Item2);
                y += 14;
            }

            y += 6;
            gfx.DrawLine(new XPen(XColors.Gray, 0.5), Margin, y, page.Width.Point - Margin, y);
            y += 12;

            // --- Messwerte / SR-Text ---
            if (config.PdfIncludeSrText && reportLines.Count > 0)
            {
                gfx.DrawString("Messwerte und Befundtext", labelFont, XBrushes.Black, new XPoint(Margin, y + 9));
                y += 18;

                var formatter = new XTextFormatter(gfx);
                foreach (var line in reportLines)
                {
                    if (y > page.Height.Point - Margin - 20)
                    {
                        gfx.Dispose();
                        page = NewPage(document, out gfx);
                        formatter = new XTextFormatter(gfx);
                        y = Margin;
                    }

                    formatter.DrawString(line, monoFont, XBrushes.Black,
                        new XRect(Margin, y, page.Width.Point - 2 * Margin, 12), XStringFormats.TopLeft);
                    y += 11;
                }

                y += 10;
            }

            // --- Bilder ---
            if (config.PdfIncludeImages && imagePaths.Count > 0)
            {
                var perPage = Math.Clamp(config.PdfImagesPerPage, 1, 9);
                var columns = perPage switch { 1 => 1, 2 => 1, 3 or 4 => 2, _ => 3 };
                var rows = (int)Math.Ceiling(perPage / (double)columns);

                var index = 0;
                foreach (var imagePath in imagePaths)
                {
                    var slot = index % perPage;
                    if (slot == 0 && (index > 0 || y > page.Height.Point / 2))
                    {
                        gfx.Dispose();
                        page = NewPage(document, out gfx);
                        y = Margin;
                    }

                    var areaWidth = (page.Width.Point - 2 * Margin) / columns;
                    var areaHeight = (page.Height.Point - y - Margin) / rows;

                    var column = slot % columns;
                    var row = slot / columns;
                    var cellX = Margin + column * areaWidth;
                    var cellY = y + row * areaHeight;

                    DrawImageFitted(gfx, imagePath, cellX + 4, cellY + 4, areaWidth - 8, areaHeight - 8);
                    index++;
                }
            }

            // Die letzte offene Zeichenfläche schließen: PdfSharp lässt pro Seite immer nur
            // ein XGraphics-Objekt gleichzeitig zu, und die Fußzeile öffnet ein neues.
            gfx.Dispose();

            // --- Fußzeile auf allen Seiten ---
            var stamp = $"Erstellt {DateTime.Now:dd.MM.yyyy HH:mm} · GDT2DICOM";
            for (var i = 0; i < document.PageCount; i++)
            {
                using var pageGfx = XGraphics.FromPdfPage(document.Pages[i]);
                pageGfx.DrawString($"{stamp} · Seite {i + 1} von {document.PageCount}", footerFont, XBrushes.Gray,
                    new XRect(Margin, document.Pages[i].Height.Point - 24, document.Pages[i].Width.Point - 2 * Margin, 12),
                    XStringFormats.TopRight);
            }

            if (pdfA)
            {
                PdfAConformance.Apply(document, new PdfADocumentInfo(
                    Title: document.Info.Title,
                    Author: string.IsNullOrWhiteSpace(config.PdfAuthor) ? config.PdfPracticeName : config.PdfAuthor,
                    Subject: header.ProcedureDescription,
                    Creator: "GDT2DICOM",
                    Created: DateTime.Now));
            }

            // Nach Save() ist das Dokument in PdfSharp gesperrt, also vorher auslesen.
            var pageCount = document.PageCount;
            document.Save(targetPath);

            var format = "PDF";
            if (pdfA)
                format = PdfAConformance.DeclareA3b(targetPath, _logger) ? "PDF/A-3b" : "PDF/A (Kennung ungeprüft)";

            _logger.LogInformation("Befundblatt erstellt: {Path} ({Format}, {Pages} Seiten, {Images} Bilder)",
                targetPath, format, pageCount, imagePaths.Count);
            return targetPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PDF {Path} konnte nicht erstellt werden.", targetPath);
            return null;
        }
    }

    /// <summary>
    /// Sucht die erste Schriftart, die sich tatsächlich auflösen lässt. Welche Schriften
    /// installiert sind, unterscheidet sich zwischen Praxisrechnern durchaus – ohne diesen
    /// Fallback bricht die PDF-Erzeugung bei einer fehlenden Schrift komplett ab.
    /// </summary>
    private static XFont SafeFont(double size, XFontStyleEx style, XPdfFontOptions? options, params string[] families)
    {
        foreach (var family in families)
        {
            try
            {
                return options is null
                    ? new XFont(family, size, style)
                    : new XFont(family, size, style, options);
            }
            catch (Exception)
            {
                // Nächste Schrift probieren.
            }
        }

        // Letzter Versuch ohne Stilangabe – schlägt auch das fehl, ist das PDF nicht erzeugbar.
        return new XFont(families.LastOrDefault() ?? "Arial", size, XFontStyleEx.Regular);
    }

    private static PdfPage NewPage(PdfDocument document, out XGraphics gfx)
    {
        var page = document.AddPage();
        page.Size = PageSize.A4;
        gfx = XGraphics.FromPdfPage(page);
        return page;
    }

    private static void DrawPair(XGraphics gfx, XFont labelFont, XFont textFont,
        double x, double y, double width, string label, string value)
    {
        gfx.DrawString(label, labelFont, XBrushes.Black, new XRect(x, y, 90, 12), XStringFormats.TopLeft);
        gfx.DrawString(value ?? "", textFont, XBrushes.Black, new XRect(x + 92, y, width - 96, 12), XStringFormats.TopLeft);
    }

    private void DrawImageFitted(XGraphics gfx, string path, double x, double y, double maxWidth, double maxHeight)
    {
        if (maxWidth <= 4 || maxHeight <= 4)
            return;

        try
        {
            using var image = XImage.FromFile(path);
            var scale = Math.Min(maxWidth / image.PixelWidth, maxHeight / image.PixelHeight);
            var width = image.PixelWidth * scale;
            var height = image.PixelHeight * scale;

            gfx.DrawImage(image, x + (maxWidth - width) / 2, y + (maxHeight - height) / 2, width, height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bild {Path} konnte nicht ins PDF übernommen werden.", path);
        }
    }
}
