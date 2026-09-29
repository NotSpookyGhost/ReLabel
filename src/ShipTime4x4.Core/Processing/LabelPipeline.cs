using ShipTime4x4.Core.Raster;
using ShipTime4x4.Core.Zpl;

namespace ShipTime4x4.Core.Processing;

public sealed record PipelineResult(byte[] Zpl, bool UsedFallback, string? WarningCode, int Width, int Height);
public sealed record PreparedLabel(GrayImage Image, byte[] Zpl, bool UsedFallback, string? WarningCode,
    int AppliedBarcodeCompensationDots = 0, int PageCount = 1);

public sealed class LabelPipeline
{
    private readonly LabelAnalyzer _analyzer;
    private readonly LabelComposer _composer;
    private readonly ZplGraphicEncoder _zplEncoder;
    private readonly BarcodeInspector _barcodeInspector;

    public LabelPipeline()
    {
        _barcodeInspector = new BarcodeInspector();
        _analyzer = new LabelAnalyzer(_barcodeInspector);
        _composer = new LabelComposer(_barcodeInspector);
        _zplEncoder = new ZplGraphicEncoder();
    }

    public PipelineResult Process(GrayImage page, int sourceDpi, int targetDpi, double marginInches)
    {
        var prepared = Prepare(page, sourceDpi, targetDpi, marginInches);
        return new PipelineResult(prepared.Zpl, prepared.UsedFallback, prepared.WarningCode,
            prepared.Image.Width, prepared.Image.Height);
    }

    public PreparedLabel Prepare(GrayImage page, int sourceDpi, int targetDpi, double marginInches)
        => Prepare(page, sourceDpi, targetDpi, marginInches, 4, 4, LabelFitMode.Proportional);

    public PreparedLabel Prepare(GrayImage page, int sourceDpi, int targetDpi, double marginInches,
        int widthInches, int heightInches, LabelFitMode fitMode)
        => Prepare(page, sourceDpi, targetDpi, marginInches, widthInches, heightInches, fitMode,
            LabelRotation.None);

    public PreparedLabel Prepare(GrayImage page, int sourceDpi, int targetDpi, double marginInches,
        int widthInches, int heightInches, LabelFitMode fitMode, LabelRotation rotation)
        => Prepare(page, sourceDpi, targetDpi, marginInches, widthInches, heightInches, fitMode, rotation,
            PrintQualityPreset.Standard, 10, 4);

    public PreparedLabel Prepare(GrayImage page, int sourceDpi, int targetDpi, double marginInches,
        int widthInches, int heightInches, LabelFitMode fitMode, LabelRotation rotation,
        PrintQualityPreset quality, double darkness, double speedIps)
        => Prepare(page, sourceDpi, targetDpi, marginInches, widthInches, heightInches, fitMode, rotation,
            quality, darkness, speedIps, RotatedBarcodeCompensation.Off, TextEnhancement.Off);

    public PreparedLabel Prepare(GrayImage page, int sourceDpi, int targetDpi, double marginInches,
        int widthInches, int heightInches, LabelFitMode fitMode, LabelRotation rotation,
        PrintQualityPreset quality, double darkness, double speedIps,
        RotatedBarcodeCompensation barcodeCompensation, TextEnhancement textEnhancement,
        IReadOnlyList<PixelRect>? pageAddressRegions = null, int fromAddressScalePercent = 100,
        int toAddressScalePercent = 100)
    {
        if (targetDpi is not (203 or 300 or 600))
            throw new ArgumentOutOfRangeException(nameof(targetDpi), "Resolution must be 203, 300, or 600 dpi.");
        var analysis = _analyzer.Analyze(page, sourceDpi, pageAddressRegions);
        if ((widthInches, heightInches) is not ((4, 4) or (4, 6) or (4, 8)))
            throw new ArgumentOutOfRangeException(nameof(heightInches), "Label size must be 4 x 4, 4 x 6, or 4 x 8 inches.");
        if (!Enum.IsDefined(rotation))
            throw new ArgumentOutOfRangeException(nameof(rotation), "Rotation must be 0, 90, 180, or 270 degrees.");
        if (!Enum.IsDefined(quality))
            throw new ArgumentOutOfRangeException(nameof(quality), "Select a supported print quality.");
        if (!Enum.IsDefined(barcodeCompensation))
            throw new ArgumentOutOfRangeException(nameof(barcodeCompensation));
        if (!Enum.IsDefined(textEnhancement))
            throw new ArgumentOutOfRangeException(nameof(textEnhancement));
        if (fromAddressScalePercent is < 100 or > 450)
            throw new ArgumentOutOfRangeException(nameof(fromAddressScalePercent));
        if (toAddressScalePercent is < 100 or > 250)
            throw new ArgumentOutOfRangeException(nameof(toAddressScalePercent));
        if (darkness is < 0 or > 30)
            throw new ArgumentOutOfRangeException(nameof(darkness), "Label darkness must be between 0 and 30.");
        GrayImage? layoutReference = null;
        if (textEnhancement != TextEnhancement.Off && analysis.AddressRegions.Count > 0)
        {
            layoutReference = analysis.NormalizedLabel;
            var enlarged = LabelPrintEnhancements.EnlargeAddressText(
                analysis.NormalizedLabel, analysis.AddressRegions, textEnhancement,
                fromAddressScalePercent, toAddressScalePercent);
            analysis = _analyzer.AnalyzeNormalized(enlarged, sourceDpi);
        }
        var composition = _composer.Compose(analysis, targetDpi, marginInches, widthInches, heightInches,
            fitMode, quality, layoutReference);
        var output = ApplyRotation(composition.Image, rotation, targetDpi, marginInches, quality);
        output = PrintRasterOptimizer.Apply(output, quality, _barcodeInspector);
        var barcodeResult = LabelPrintEnhancements.CompensateRotatedBarcodesWithResult(
            output, rotation, barcodeCompensation, _barcodeInspector);
        output = barcodeResult.Image;
        var threshold = ImageAnalysis.OtsuThreshold(output);
        var zpl = _zplEncoder.Encode(output, threshold, darkness, speedIps);
        return new PreparedLabel(output, zpl, composition.UsedFallback, composition.WarningCode,
            barcodeResult.AppliedDots);
    }

    public PreparedLabel PrepareDefined(GrayImage page, int sourceDpi, int targetDpi, double marginInches,
        int widthInches, int heightInches, LabelFitMode fitMode, LabelRotation rotation,
        PrintQualityPreset quality, double darkness, double speedIps,
        RotatedBarcodeCompensation barcodeCompensation, TextEnhancement textEnhancement,
        IReadOnlyList<DefinedLayoutRegion> regions, IReadOnlyList<PixelRect> addressRegions,
        int fromAddressScalePercent, int toAddressScalePercent)
    {
        if (targetDpi is not (203 or 300 or 600)) throw new ArgumentOutOfRangeException(nameof(targetDpi));
        var enhanced = textEnhancement == TextEnhancement.Off ? page : LabelPrintEnhancements.EnlargeAddressText(
            page, addressRegions, textEnhancement, fromAddressScalePercent, toAddressScalePercent);
        // Address enlargement may legitimately move ink outside its original marked source
        // rectangle. If that makes any required extraction blank, compose from the original
        // page rather than rejecting an otherwise valid template.
        var compositionSource = regions.Where(x => x.Required).All(region =>
            !ImageAnalysis.FindContentBounds(enhanced.Crop(region.Source), padding: 0).IsEmpty)
            ? enhanced : page;
        var composition = _composer.ComposeDefined(compositionSource, regions, targetDpi, marginInches,
            widthInches, heightInches, fitMode, quality);
        var finalBarcodeRegions = TransformRegionsForRotation(composition.BarcodeRegions ?? [],
            composition.Image.Width, composition.Image.Height, rotation, targetDpi, marginInches);
        var output = ApplyRotation(composition.Image, rotation, targetDpi, marginInches, quality);
        output = PrintRasterOptimizer.Apply(output, quality, _barcodeInspector);
        var barcodeResult = LabelPrintEnhancements.CompensateRotatedBarcodesWithResult(
            output, rotation, barcodeCompensation, _barcodeInspector);
        output = barcodeResult.Image;
        if (!composition.UsedFallback && !VerifyMarkedBarcodes(page, output, regions, finalBarcodeRegions))
        {
            var fallback = Prepare(page, sourceDpi, targetDpi, marginInches, widthInches, heightInches,
                fitMode, rotation, quality, darkness, speedIps, barcodeCompensation, textEnhancement,
                addressRegions, fromAddressScalePercent, toAddressScalePercent);
            return fallback with { UsedFallback = true,
                WarningCode = "template-final-barcode-verification-failed" };
        }
        var zpl = _zplEncoder.Encode(output, ImageAnalysis.OtsuThreshold(output), darkness, speedIps);
        return new PreparedLabel(output, zpl, composition.UsedFallback, composition.WarningCode,
            barcodeResult.AppliedDots);
    }

    private bool VerifyMarkedBarcodes(GrayImage source, GrayImage output,
        IReadOnlyList<DefinedLayoutRegion> regions, IReadOnlyList<PixelRect>? outputBarcodeRegions = null)
    {
        var expected = _barcodeInspector.PayloadsInRegions(source,
            regions.Where(region => region.Barcode).Select(region => region.Source).ToArray());
        return _barcodeInspector.OutputContainsPayloads(output, expected, outputBarcodeRegions);
    }

    private static IReadOnlyList<PixelRect> TransformRegionsForRotation(
        IReadOnlyList<PixelRect> regions, int imageWidth, int imageHeight, LabelRotation rotation,
        int targetDpi, double marginInches)
    {
        if (regions.Count == 0 || rotation == LabelRotation.None) return regions;
        var margin = Math.Max(1, (int)Math.Ceiling(targetDpi * Math.Max(0.125, marginInches)));
        var innerWidth = imageWidth - margin * 2;
        var innerHeight = imageHeight - margin * 2;
        var rotatedWidth = rotation is LabelRotation.Clockwise90 or LabelRotation.Clockwise270
            ? innerHeight : innerWidth;
        var rotatedHeight = rotation is LabelRotation.Clockwise90 or LabelRotation.Clockwise270
            ? innerWidth : innerHeight;
        var scale = Math.Min(innerWidth / (double)rotatedWidth, innerHeight / (double)rotatedHeight);
        var outputWidth = Math.Max(1, (int)Math.Round(rotatedWidth * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(rotatedHeight * scale));
        var offsetX = margin + (innerWidth - outputWidth) / 2;
        var offsetY = margin + (innerHeight - outputHeight) / 2;
        return regions.Select(region =>
        {
            var x = region.X - margin;
            var y = region.Y - margin;
            var transformed = rotation switch
            {
                LabelRotation.Clockwise90 => new PixelRect(innerHeight - region.Height - y,
                    x, region.Height, region.Width),
                LabelRotation.UpsideDown180 => new PixelRect(innerWidth - region.Width - x,
                    innerHeight - region.Height - y, region.Width, region.Height),
                LabelRotation.Clockwise270 => new PixelRect(y, innerWidth - region.Width - x,
                    region.Height, region.Width),
                _ => new PixelRect(x, y, region.Width, region.Height)
            };
            return new PixelRect(offsetX + (int)Math.Floor(transformed.X * scale),
                offsetY + (int)Math.Floor(transformed.Y * scale),
                Math.Max(1, (int)Math.Ceiling(transformed.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(transformed.Height * scale)))
                .Inflate(2, 2, imageWidth, imageHeight);
        }).ToArray();
    }

    private static GrayImage ApplyRotation(GrayImage image, LabelRotation rotation, int targetDpi,
        double marginInches, PrintQualityPreset quality)
    {
        if (rotation == LabelRotation.None) return image;
        var margin = Math.Max(1, (int)Math.Ceiling(targetDpi * Math.Max(0.125, marginInches)));
        var innerWidth = image.Width - (2 * margin);
        var innerHeight = image.Height - (2 * margin);
        var inner = image.Crop(new PixelRect(margin, margin, innerWidth, innerHeight));
        var rotated = inner.RotateClockwise((int)rotation);
        var scale = Math.Min(innerWidth / (double)rotated.Width, innerHeight / (double)rotated.Height);
        var width = Math.Max(1, (int)Math.Round(rotated.Width * scale));
        var height = Math.Max(1, (int)Math.Round(rotated.Height * scale));
        var output = GrayImage.White(image.Width, image.Height);
        var resized = quality == PrintQualityPreset.HighQuality
            ? rotated.ResizeBilinear(width, height)
            : rotated.Resize(width, height);
        output.Blit(resized, margin + ((innerWidth - width) / 2),
            margin + ((innerHeight - height) / 2));
        return output;
    }
}
