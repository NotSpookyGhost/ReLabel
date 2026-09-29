using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using PDFtoImage;
using ShipTime4x4.Core.Processing;
using ShipTime4x4.Core.Raster;
using ShipTime4x4.Hotfolder.Models;
using TesseractOCR;
using TesseractOCR.Enums;

namespace ShipTime4x4.Hotfolder.Services;

public sealed record PdfInspection(
    GrayImage SourcePage,
    byte[] SourcePng,
    string RecognizedText,
    string RecognizedTsv,
    float OcrConfidence,
    int PageCount = 1);

public sealed class PdfLabelProcessor
{
    public const int SourceRenderDpi = 300;
    private readonly string _tessDataPath;
    private readonly LabelPipeline _pipeline = new();
    private readonly ConcurrentDictionary<string, Lazy<PdfInspection>> _inspectionCache = new();

    public PdfLabelProcessor(string applicationFolder)
    {
        _tessDataPath = Path.Combine(applicationFolder, "tessdata");
    }

    public PdfInspection Inspect(string pdfPath)
    {
        var key = FileCacheKey(pdfPath);
        var lazy = _inspectionCache.GetOrAdd(key,
            _ => new Lazy<PdfInspection>(() => InspectCore(pdfPath), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var inspection = lazy.Value;
            TrimInspectionCache(key);
            return inspection;
        }
        catch
        {
            _inspectionCache.TryRemove(key, out _);
            throw;
        }
    }

    public void RememberInspection(string pdfPath, PdfInspection inspection)
    {
        var key = FileCacheKey(pdfPath);
        _inspectionCache[key] = new Lazy<PdfInspection>(() => inspection);
        TrimInspectionCache(key);
    }

    private PdfInspection InspectCore(string pdfPath)
    {
        var pdf = File.ReadAllBytes(pdfPath);
        var pageCount = Conversion.GetPageCount(pdf, password: null);
        if (pageCount < 1)
            throw new InvalidOperationException("The PDF does not contain a label page.");

        using var pngStream = new MemoryStream();
        Conversion.SavePng(pngStream, pdf, page: 0, password: null,
            options: new RenderOptions { Dpi = SourceRenderDpi, Grayscale = false, WithAnnotations = true });
        var png = pngStream.ToArray();
        var page = DecodeGrayImage(png);
        var (text, tsv, confidence) = Recognize(png);
        return new PdfInspection(page, png, text, tsv, confidence, pageCount);
    }

    public PreparedLabel Prepare(string pdfPath, int targetDpi, double marginInches)
        => Prepare(pdfPath, targetDpi, marginInches, 4, 4, LabelFitMode.Proportional,
            LabelRotation.None, PrintQualityPreset.Standard, 10, 4,
            RotatedBarcodeCompensation.Off, TextEnhancement.Off);

    public PreparedLabel Prepare(string pdfPath, int targetDpi, double marginInches,
        int widthInches, int heightInches, LabelFitMode fitMode)
        => Prepare(pdfPath, targetDpi, marginInches, widthInches, heightInches, fitMode, LabelRotation.None);

    public PreparedLabel Prepare(string pdfPath, int targetDpi, double marginInches,
        int widthInches, int heightInches, LabelFitMode fitMode, LabelRotation rotation)
        => Prepare(pdfPath, targetDpi, marginInches, widthInches, heightInches, fitMode, rotation,
            PrintQualityPreset.Standard, 10, 4);

    public PreparedLabel Prepare(string pdfPath, int targetDpi, double marginInches,
        int widthInches, int heightInches, LabelFitMode fitMode, LabelRotation rotation,
        PrintQualityPreset quality, double darkness, double speedIps)
        => Prepare(pdfPath, targetDpi, marginInches, widthInches, heightInches, fitMode, rotation,
            quality, darkness, speedIps, RotatedBarcodeCompensation.Off, TextEnhancement.Off);

    public PreparedLabel Prepare(string pdfPath, int targetDpi, double marginInches,
        int widthInches, int heightInches, LabelFitMode fitMode, LabelRotation rotation,
        PrintQualityPreset quality, double darkness, double speedIps,
        RotatedBarcodeCompensation barcodeCompensation, TextEnhancement textEnhancement,
        int fromAddressScalePercent = 100, int toAddressScalePercent = 100)
    {
        var sourceDpi = SourceDpiFor(quality, targetDpi);
        var pageCount = GetPageCount(pdfPath);
        var inspection = textEnhancement == TextEnhancement.Off ? null : Inspect(pdfPath);
        var located = inspection is null
            ? []
            : ShippingAddressRegionLocator.Locate(inspection.RecognizedTsv,
                inspection.SourcePage.Width, inspection.SourcePage.Height, SourceRenderDpi);
        var preparedPages = new List<PreparedLabel>(pageCount);
        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var page = pageIndex == 0 && inspection is not null && sourceDpi == SourceRenderDpi
                ? inspection.SourcePage : RenderPage(pdfPath, sourceDpi, pageIndex);
            var addressRegions = inspection is null
                ? []
                : ScaleRegions(located, inspection.SourcePage.Width, inspection.SourcePage.Height,
                    page.Width, page.Height);
            preparedPages.Add(_pipeline.Prepare(page, sourceDpi, targetDpi, marginInches,
                widthInches, heightInches, fitMode, rotation, quality, darkness, speedIps,
                barcodeCompensation, textEnhancement, addressRegions, fromAddressScalePercent,
                toAddressScalePercent));
        }
        return CombinePages(preparedPages);
    }

    public PreparedLabel Prepare(string pdfPath, int targetDpi, double marginInches,
        int widthInches, int heightInches, LabelFitMode fitMode, LabelRotation rotation,
        PrintQualityPreset quality, double darkness, double speedIps,
        RotatedBarcodeCompensation barcodeCompensation, TextEnhancement textEnhancement,
        int fromAddressScalePercent, int toAddressScalePercent, AppliedTemplateSnapshot? template)
    {
        if (template is null)
            return Prepare(pdfPath, targetDpi, marginInches, widthInches, heightInches, fitMode, rotation,
                quality, darkness, speedIps, barcodeCompensation, textEnhancement,
                fromAddressScalePercent, toAddressScalePercent);
        var sourceDpi = SourceDpiFor(quality, targetDpi);
        var pageCount = GetPageCount(pdfPath);
        var preparedPages = new List<PreparedLabel>(pageCount);
        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var page = CarrierTemplateEngine.NormalizePortrait(RenderPage(pdfPath, sourceDpi, pageIndex));
            var isFourByFour = widthInches == 4 && heightInches == 4;
            var usableZones = isFourByFour
                ? template.Zones.Where(zone => zone.IsPlaced4x4).ToArray()
                : template.Zones.Where(zone => zone.HasSourceRegion).ToArray();
            var regions = usableZones.Select((zone, zOrder) => new DefinedLayoutRegion(
                zone.HasSourceRegion ? zone.Source.ToPixels(page.Width, page.Height) : new PixelRect(0, 0, 1, 1),
                isFourByFour ? ToInnerDestination(zone.Destination4x4) : null,
                Priority(zone), PreserveAspect(zone), Required(zone), zone.DisplayName,
                zone.Type is TemplateZoneType.CarrierBarcode or TemplateZoneType.ShippingBarcode,
                zone.Type == TemplateZoneType.ShippingBarcode || zone.IsGenerated,
                zOrder, zone.IsGenerated ? TemplateGeneratedContent.Render(zone, sourceDpi) : null)).ToArray();
            var addresses = usableZones
                .Where(zone => zone.Type is TemplateZoneType.FromAddress or TemplateZoneType.ToAddress)
                .Select(zone => zone.Source.ToPixels(page.Width, page.Height)).ToArray();
            preparedPages.Add(_pipeline.PrepareDefined(page, sourceDpi, targetDpi, marginInches,
                widthInches, heightInches, fitMode, rotation, quality, darkness, speedIps,
                barcodeCompensation, textEnhancement, regions, addresses,
                fromAddressScalePercent, toAddressScalePercent));
        }
        return CombinePages(preparedPages);
    }

    public PreparedLabel Prepare(PdfInspection inspection, int targetDpi, double marginInches) =>
        _pipeline.Prepare(inspection.SourcePage, SourceRenderDpi, targetDpi, marginInches);

    private static int SourceDpiFor(PrintQualityPreset quality, int targetDpi) => quality switch
    {
        PrintQualityPreset.Fast => targetDpi,
        PrintQualityPreset.Standard => Math.Max(SourceRenderDpi, targetDpi),
        // A 300-DPI source already oversamples a 203-DPI printhead and avoids an expensive
        // second 600-DPI PDF render. Native 600-DPI output still renders at 600.
        PrintQualityPreset.HighQuality => targetDpi >= 600 ? 600 : Math.Max(SourceRenderDpi, targetDpi),
        _ => throw new ArgumentOutOfRangeException(nameof(quality))
    };

    private static NormalizedLayoutRect ToInnerDestination(NormalizedRect destination)
    {
        const double safe = .03125;
        const double usable = 1 - safe * 2;
        return new NormalizedLayoutRect((destination.X - safe) / usable, (destination.Y - safe) / usable,
            destination.Width / usable, destination.Height / usable);
    }

    private static BlockPriority Priority(TemplateZone zone) => zone.Type switch
    {
        TemplateZoneType.CarrierBarcode or TemplateZoneType.ShippingBarcode => BlockPriority.ScanCritical,
        TemplateZoneType.OtherSection when zone.OtherPriority == TemplateOtherPriority.Optional => BlockPriority.Optional,
        TemplateZoneType.OtherSection when zone.OtherPriority == TemplateOtherPriority.Flexible => BlockPriority.Flexible,
        _ => BlockPriority.Required
    };

    private static bool Required(TemplateZone zone) =>
        !zone.IsGenerated && (zone.Type != TemplateZoneType.OtherSection ||
                              zone.OtherPriority == TemplateOtherPriority.Required);

    private static bool PreserveAspect(TemplateZone zone) =>
        zone.Type is TemplateZoneType.CarrierBarcode or TemplateZoneType.ShippingBarcode or TemplateZoneType.CarrierLogo;

    public IReadOnlyList<string> DetectBarcodePayloads(string pdfPath)
    {
        var pageCount = GetPageCount(pdfPath);
        var inspector = new BarcodeInspector();
        return Enumerable.Range(0, pageCount)
            .SelectMany(pageIndex => inspector.Detect(RenderPage(pdfPath, SourceRenderDpi, pageIndex)))
            .Select(region => region.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static PreparedLabel CombinePages(IReadOnlyList<PreparedLabel> pages)
    {
        if (pages.Count == 0) throw new InvalidOperationException("The PDF does not contain a label page.");
        if (pages.Count == 1) return pages[0] with { PageCount = 1 };
        var totalBytes = pages.Sum(page => (long)page.Zpl.Length);
        if (totalBytes > int.MaxValue) throw new InvalidOperationException("The prepared print job is too large.");
        var zpl = new byte[(int)totalBytes];
        var offset = 0;
        foreach (var page in pages)
        {
            Buffer.BlockCopy(page.Zpl, 0, zpl, offset, page.Zpl.Length);
            offset += page.Zpl.Length;
        }
        var warnings = pages.Select((page, index) => page.WarningCode is null
                ? null : $"page-{index + 1}:{page.WarningCode}")
            .Where(value => value is not null);
        return new PreparedLabel(pages[0].Image, zpl, pages.Any(page => page.UsedFallback),
            string.Join(",", warnings) is { Length: > 0 } warning ? warning : null,
            pages.Max(page => page.AppliedBarcodeCompensationDots), pages.Count);
    }

    private static int GetPageCount(string pdfPath)
    {
        var count = Conversion.GetPageCount(File.ReadAllBytes(pdfPath), password: null);
        return count > 0 ? count : throw new InvalidOperationException("The PDF does not contain a label page.");
    }

    private static GrayImage RenderPage(string pdfPath, int dpi, int pageIndex = 0)
    {
        var pdf = File.ReadAllBytes(pdfPath);
        var pageCount = Conversion.GetPageCount(pdf, password: null);
        if (pageIndex < 0 || pageIndex >= pageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        using var pngStream = new MemoryStream();
        Conversion.SavePng(pngStream, pdf, page: pageIndex, password: null,
            options: new RenderOptions { Dpi = dpi, Grayscale = false, WithAnnotations = true });
        return DecodeGrayImage(pngStream.ToArray());
    }

    private static string FileCacheKey(string path)
    {
        var info = new FileInfo(path);
        return $"{info.FullName.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private void TrimInspectionCache(string currentKey)
    {
        if (_inspectionCache.Count <= 12) return;
        foreach (var key in _inspectionCache.Keys.Where(key => key != currentKey).Take(_inspectionCache.Count - 12))
            _inspectionCache.TryRemove(key, out _);
    }

    private static IReadOnlyList<PixelRect> ScaleRegions(IReadOnlyList<PixelRect> regions,
        int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth == targetWidth && sourceHeight == targetHeight) return regions;
        var scaleX = targetWidth / (double)sourceWidth;
        var scaleY = targetHeight / (double)sourceHeight;
        return regions.Select(region =>
        {
            var left = (int)Math.Round(region.X * scaleX);
            var top = (int)Math.Round(region.Y * scaleY);
            var right = (int)Math.Round(region.Right * scaleX);
            var bottom = (int)Math.Round(region.Bottom * scaleY);
            return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        }).ToArray();
    }

    private (string Text, string Tsv, float Confidence) Recognize(byte[] png)
    {
        if (!File.Exists(Path.Combine(_tessDataPath, "eng.traineddata")))
            return (string.Empty, string.Empty, 0);
        using var engine = new Engine(_tessDataPath, Language.English, EngineMode.LstmOnly);
        using var image = TesseractOCR.Pix.Image.LoadFromMemory(png);
        using var page = engine.Process(image, PageSegMode.SparseText);
        return (page.Text ?? string.Empty, page.TsvText ?? string.Empty, page.MeanConfidence);
    }

    private static GrayImage DecodeGrayImage(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        using var loaded = new Bitmap(stream);
        using var bitmap = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.DrawImageUnscaled(loaded, 0, 0);
        }

        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var buffer = new byte[checked(stride * bitmap.Height)];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            var pixels = new byte[checked(bitmap.Width * bitmap.Height)];
            for (var y = 0; y < bitmap.Height; y++)
            {
                var sourceRow = data.Stride >= 0 ? y * stride : (bitmap.Height - 1 - y) * stride;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var offset = sourceRow + (x * 4);
                    var blue = buffer[offset];
                    var green = buffer[offset + 1];
                    var red = buffer[offset + 2];
                    pixels[(y * bitmap.Width) + x] = (byte)((red * 77 + green * 150 + blue * 29) >> 8);
                }
            }
            return new GrayImage(bitmap.Width, bitmap.Height, pixels);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}

public static class GrayImageBitmapConverter
{
    public static Bitmap ToBitmap(GrayImage image)
    {
        var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, image.Width, image.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var buffer = Enumerable.Repeat((byte)255, checked(stride * image.Height)).ToArray();
            for (var y = 0; y < image.Height; y++)
            {
                var destinationRow = data.Stride >= 0 ? y * stride : (image.Height - 1 - y) * stride;
                for (var x = 0; x < image.Width; x++)
                {
                    var value = image[x, y];
                    var offset = destinationRow + (x * 3);
                    buffer[offset] = value;
                    buffer[offset + 1] = value;
                    buffer[offset + 2] = value;
                }
            }
            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }
}
