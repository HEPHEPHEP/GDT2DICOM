using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Gdt2Dicom.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Export;

public sealed record ExportedImage(string Path, int FrameIndex, string SopInstanceUid);

/// <summary>Rendert DICOM-Pixeldaten nach JPEG oder PNG.</summary>
public sealed class ImageExporter
{
    private readonly ILogger _logger;

    public ImageExporter(ILogger logger) => _logger = logger;

    /// <summary>
    /// Exportiert alle Bilder einer Datei. Multiframe-Objekte werden je nach Konfiguration
    /// nur mit dem ersten Frame exportiert – ein Cine-Loop mit 200 Frames als 200 JPEGs
    /// ins PVS zu schieben, hilft niemandem.
    /// </summary>
    public List<ExportedImage> Export(DicomFile file, ExportConfig config, string targetDirectory,
        string fileNameBase, int startIndex)
    {
        var results = new List<ExportedImage>();

        if (config.ImageFormat == ImageOutputFormat.None)
            return results;

        var dataset = file.Dataset;
        if (!dataset.Contains(DicomTag.PixelData))
            return results;

        var sopInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);

        try
        {
            Directory.CreateDirectory(targetDirectory);

            var image = new DicomImage(dataset);
            var frameCount = config.FirstFrameOnlyForMultiframe ? 1 : image.NumberOfFrames;
            frameCount = Math.Max(1, Math.Min(frameCount, image.NumberOfFrames));

            var extension = config.ImageFormat == ImageOutputFormat.Png ? ".png" : ".jpg";

            for (var frame = 0; frame < frameCount; frame++)
            {
                var index = startIndex + results.Count;
                var path = Path.Combine(targetDirectory, $"{fileNameBase}_{index:D3}{extension}");

                using var rendered = image.RenderImage(frame);
                using var bitmap = rendered.AsClonedBitmap();
                using var scaled = Scale(bitmap, config.MaxImageWidth);

                Save(scaled, path, config);
                results.Add(new ExportedImage(path, frame, sopInstanceUid));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bild {Sop} konnte nicht exportiert werden (Transfersyntax {Ts}).",
                sopInstanceUid, file.FileMetaInfo?.TransferSyntax?.UID?.Name);
        }

        return results;
    }

    private static Bitmap Scale(Bitmap source, int maxWidth)
    {
        if (maxWidth <= 0 || source.Width <= maxWidth)
            return (Bitmap)source.Clone();

        var height = (int)Math.Round(source.Height * (maxWidth / (double)source.Width));
        var target = new Bitmap(maxWidth, Math.Max(1, height));

        using var graphics = Graphics.FromImage(target);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(source, 0, 0, target.Width, target.Height);

        return target;
    }

    private static void Save(Bitmap bitmap, string path, ExportConfig config)
    {
        if (config.ImageFormat == ImageOutputFormat.Png)
        {
            bitmap.Save(path, ImageFormat.Png);
            return;
        }

        var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.FormatID == ImageFormat.Jpeg.Guid);
        if (encoder is null)
        {
            bitmap.Save(path, ImageFormat.Jpeg);
            return;
        }

        var quality = Math.Clamp(config.JpegQuality, 10, 100);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        bitmap.Save(path, encoder, parameters);
    }
}
