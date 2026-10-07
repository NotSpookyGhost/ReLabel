using ShipTime4x4.Core.Raster;

namespace ShipTime4x4.Core.Processing;

public sealed record CompositionResult(GrayImage Image, bool UsedFallback, string? WarningCode,
    IReadOnlyList<PixelRect>? BarcodeRegions = null);

public readonly record struct NormalizedLayoutRect(double X, double Y, double Width, double Height);

public sealed record DefinedLayoutRegion(
    PixelRect Source,
    NormalizedLayoutRect? Destination,
    BlockPriority Priority,
    bool PreserveAspect,
    bool Required,
    string Name = "required zone",
    bool Barcode = false,
    bool StretchToDestination = false,
    int ZOrder = 0,
    GrayImage? ContentOverride = null,
    bool StripSourceHeaderFragment = false);

public enum LabelFitMode
{
    Proportional,
    SquishToFill
}

public sealed class LabelComposer(BarcodeInspector barcodeInspector)
{
    private const double MinimumSafeMarginInches = 0.125;

    public CompositionResult Compose(LabelAnalysis analysis, int targetDpi, double marginInches,
        int widthInches, int heightInches, LabelFitMode fitMode, PrintQualityPreset quality,
        GrayImage? layoutReference = null)
    {
        var width = checked(targetDpi * widthInches);
        var height = checked(targetDpi * heightInches);
        if (fitMode == LabelFitMode.SquishToFill)
            return Squish(analysis.NormalizedLabel, layoutReference, width, height, targetDpi, marginInches, quality);

        if (!analysis.IsConfident || analysis.Blocks.Count == 0)
            return Fallback(analysis.NormalizedLabel, layoutReference, width, height, targetDpi, marginInches, quality,
                analysis.WarningCode ?? "layout-low-confidence");

        if (!TryComposeBlocks(analysis, width, height, targetDpi, marginInches, quality, out var composed))
            return Fallback(analysis.NormalizedLabel, layoutReference, width, height, targetDpi, marginInches, quality, "layout-does-not-fit");

        if (!barcodeInspector.ContainsSamePayloads(analysis.NormalizedLabel, composed))
            return Fallback(analysis.NormalizedLabel, layoutReference, width, height, targetDpi, marginInches, quality,
                "barcode-verification-failed");

        return new CompositionResult(composed, false, null);
    }

    public CompositionResult ComposeDefined(GrayImage source, IReadOnlyList<DefinedLayoutRegion> regions,
        int targetDpi, double marginInches, int widthInches, int heightInches,
        LabelFitMode fitMode, PrintQualityPreset quality)
    {
        if (widthInches != 4 || heightInches != 4 || regions.Any(x => x.Destination is null))
        {
            var blocks = regions.Select((region, index) =>
                new LayoutBlock(region.Source, region.Priority, index)).ToArray();
            var analysis = new LabelAnalysis(source, blocks, barcodeInspector.Detect(source), [], true, null);
            return Compose(analysis, targetDpi, marginInches, widthInches, heightInches, fitMode, quality);
        }

        var canvasWidth = checked(targetDpi * widthInches);
        var canvasHeight = checked(targetDpi * heightInches);
        var margin = SafeMargin(targetDpi, marginInches);
        var innerWidth = canvasWidth - margin * 2;
        var innerHeight = canvasHeight - margin * 2;
        var canvas = GrayImage.White(canvasWidth, canvasHeight);
        var barcodeDestinations = new List<PixelRect>();
        // Template order is the explicit layer stack. The final barcode check still
        // prevents a higher layer from silently making a marked barcode unreadable.
        foreach (var region in regions.OrderBy(x => x.ZOrder))
        {
            var crop = region.ContentOverride ?? source.Crop(region.Source);
            if (region.StripSourceHeaderFragment && region.ContentOverride is null)
                StripPartialHeaderFromAddress(crop, source.Width, source.Height);
            if (region.Required && ImageAnalysis.FindContentBounds(crop, padding: 0).IsEmpty)
                return Fallback(source, null, canvasWidth, canvasHeight, targetDpi, marginInches,
                    quality, $"template-required-zone-blank:{region.Name}");
            if (ImageAnalysis.FindContentBounds(crop, padding: 0).IsEmpty) continue;
            var normalized = region.Destination!.Value;
            var box = new PixelRect(
                margin + (int)Math.Round(normalized.X * innerWidth),
                margin + (int)Math.Round(normalized.Y * innerHeight),
                Math.Max(1, (int)Math.Round(normalized.Width * innerWidth)),
                Math.Max(1, (int)Math.Round(normalized.Height * innerHeight)));
            var scale = Math.Min(box.Width / (double)crop.Width, box.Height / (double)crop.Height);
            var width = region.StretchToDestination ? box.Width : region.PreserveAspect || fitMode == LabelFitMode.Proportional
                ? Math.Max(1, (int)Math.Round(crop.Width * scale)) : box.Width;
            var height = region.StretchToDestination ? box.Height : region.PreserveAspect || fitMode == LabelFitMode.Proportional
                ? Math.Max(1, (int)Math.Round(crop.Height * scale)) : box.Height;
            width = Math.Min(width, box.Width); height = Math.Min(height, box.Height);
            var resized = Resize(crop, width, height, quality);
            // Dense barcodes can become undecodable at 203 DPI when strict proportional
            // fitting leaves most of a deliberately wide destination box unused. Use the
            // full user-drawn box only as a verified rescue: the stretched candidate must
            // decode to the payload found in this marked source zone before it is accepted.
            if (region.Barcode && (width != box.Width || height != box.Height))
            {
                var expected = barcodeInspector.PayloadsInRegions(source, [region.Source]);
                if (expected.Count > 0 && !barcodeInspector.OutputContainsPayloads(resized, expected))
                {
                    var fitted = Resize(crop, box.Width, box.Height, quality);
                    if (barcodeInspector.OutputContainsPayloads(fitted, expected))
                    {
                        resized = fitted;
                        width = box.Width;
                        height = box.Height;
                    }
                }
            }
            var drawX = box.X + (box.Width - width) / 2;
            var drawY = box.Y + (box.Height - height) / 2;
            canvas.Blit(resized, drawX, drawY);
            if (region.Barcode)
                barcodeDestinations.Add(new PixelRect(drawX, drawY, width, height));
        }

        if (!VerifyMarkedBarcodes(source, canvas, regions, barcodeDestinations))
            return Fallback(source, null, canvasWidth, canvasHeight, targetDpi, marginInches,
                quality, "template-barcode-verification-failed");
        return new CompositionResult(canvas, false, null, barcodeDestinations);
    }

    internal static void StripPartialHeaderFromAddress(GrayImage crop, int sourceWidth, int sourceHeight)
    {
        // A source address box can begin inside the printed FROM/TO header. When a
        // generated header is also placed, that partial black strip is duplicated.
        // Only remove it if a wide, dense strip is followed by a blank separator.
        var minimumRun = Math.Max(20, sourceWidth / 20);
        var probeHeight = Math.Min(crop.Height, Math.Max(2, sourceHeight / 100));
        var denseRows = 0;
        var firstDenseRow = -1;
        for (var y = 0; y < probeHeight; y++)
        {
            var longestRun = 0;
            var run = 0;
            for (var x = 0; x < crop.Width; x++)
            {
                run = crop[x, y] < 100 ? run + 1 : 0;
                longestRun = Math.Max(longestRun, run);
            }
            if (longestRun < minimumRun) continue;
            firstDenseRow = firstDenseRow < 0 ? y : firstDenseRow;
            denseRows++;
        }
        if (denseRows < 2) return;

        var scanHeight = Math.Min(crop.Height, Math.Max(probeHeight + 2, sourceHeight / 40));
        for (var y = firstDenseRow + 1; y + 1 < scanHeight; y++)
        {
            if (!IsNearlyBlankRow(crop, y) || !IsNearlyBlankRow(crop, y + 1)) continue;
            Array.Fill(crop.Pixels, (byte)255, 0, (y + 2) * crop.Width);
            return;
        }
    }

    private static bool IsNearlyBlankRow(GrayImage image, int y)
    {
        var dark = 0;
        for (var x = 0; x < image.Width; x++)
            if (image[x, y] < 235 && ++dark > Math.Max(1, image.Width / 100)) return false;
        return true;
    }

    private bool VerifyMarkedBarcodes(GrayImage source, GrayImage output,
        IReadOnlyList<DefinedLayoutRegion> regions, IReadOnlyList<PixelRect> outputBarcodeRegions)
    {
        var expected = barcodeInspector.PayloadsInRegions(source,
            regions.Where(region => region.Barcode).Select(region => region.Source).ToArray());
        return barcodeInspector.OutputContainsPayloads(output, expected, outputBarcodeRegions);
    }

    private static bool TryComposeBlocks(
        LabelAnalysis analysis,
        int canvasWidth,
        int canvasHeight,
        int targetDpi,
        double marginInches,
        PrintQualityPreset quality,
        out GrayImage image)
    {
        image = GrayImage.White(canvasWidth, canvasHeight);
        var margin = SafeMargin(targetDpi, marginInches);
        var gap = Math.Max(2, (int)Math.Round(targetDpi * 0.025));
        var availableWidth = canvasWidth - (2 * margin);
        var availableHeight = canvasHeight - (2 * margin);
        var critical = analysis.Blocks.Where(x => x.Priority == BlockPriority.ScanCritical)
            .OrderBy(x => x.SourceOrder).ToArray();
        var supporting = analysis.Blocks.Where(x => x.Priority is BlockPriority.Required or BlockPriority.Flexible)
            .OrderBy(x => x.SourceOrder).ToArray();
        var optional = analysis.Blocks.Where(x => x.Priority == BlockPriority.Optional)
            .OrderBy(x => x.SourceOrder).ToArray();
        if (critical.Length == 0 || supporting.Length == 0)
            return false;

        var criticalScale = Math.Min(1d, critical.Min(block => availableWidth / (double)block.Bounds.Width));
        var criticalHeight = critical.Sum(block => Math.Max(1, (int)Math.Round(block.Bounds.Height * criticalScale))) +
                             gap * Math.Max(0, critical.Length - 1);
        var supportAvailableHeight = availableHeight - criticalHeight - gap;
        if (supportAvailableHeight <= 0)
            return false;

        var supportScale = Math.Min(1d, Math.Min(
            (availableWidth - gap * Math.Max(0, supporting.Length - 1)) /
            (double)supporting.Sum(block => block.Bounds.Width),
            supportAvailableHeight / (double)supporting.Max(block => block.Bounds.Height)));
        if (supportScale < 0.28)
            return false;

        var supportHeight = Math.Max(1, (int)Math.Round(supporting.Max(block => block.Bounds.Height) * supportScale));
        var usedWidth = supporting.Sum(block => Math.Max(1, (int)Math.Round(block.Bounds.Width * supportScale))) +
                        gap * Math.Max(0, supporting.Length - 1);
        var x = margin + Math.Max(0, (availableWidth - usedWidth) / 2);
        var y = margin;
        foreach (var block in supporting)
        {
            var width = Math.Max(1, (int)Math.Round(block.Bounds.Width * supportScale));
            var height = Math.Max(1, (int)Math.Round(block.Bounds.Height * supportScale));
            var resized = Resize(analysis.NormalizedLabel.Crop(block.Bounds), width, height, quality);
            image.Blit(resized, x, y + ((supportHeight - height) / 2));
            x += width + gap;
        }

        y += supportHeight + gap;
        foreach (var block in critical)
        {
            var width = Math.Max(1, (int)Math.Round(block.Bounds.Width * criticalScale));
            var height = Math.Max(1, (int)Math.Round(block.Bounds.Height * criticalScale));
            var resized = Resize(analysis.NormalizedLabel.Crop(block.Bounds), width, height, quality);
            image.Blit(resized, margin + ((availableWidth - width) / 2), y);
            y += height + gap;
        }

        var remainingHeight = margin + availableHeight - y;
        foreach (var block in optional)
        {
            if (remainingHeight <= gap)
                break;
            var scale = Math.Min(availableWidth / (double)block.Bounds.Width,
                (remainingHeight - gap) / (double)block.Bounds.Height);
            if (scale < 0.4)
                continue;
            var width = Math.Max(1, (int)Math.Round(block.Bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Round(block.Bounds.Height * scale));
            image.Blit(Resize(analysis.NormalizedLabel.Crop(block.Bounds), width, height, quality),
                margin + ((availableWidth - width) / 2), y);
            y += height + gap;
            remainingHeight = margin + availableHeight - y;
        }

        return y <= canvasHeight - margin + gap;
    }

    private static CompositionResult Fallback(
        GrayImage label,
        GrayImage? layoutReference,
        int canvasWidth,
        int canvasHeight,
        int targetDpi,
        double marginInches,
        PrintQualityPreset quality,
        string warningCode)
    {
        var margin = SafeMargin(targetDpi, marginInches);
        var availableWidth = canvasWidth - (2 * margin);
        var availableHeight = canvasHeight - (2 * margin);
        var compacted = layoutReference is null
            ? ImageAnalysis.CompactVerticalWhitespace(label)
            : ImageAnalysis.CompactVerticalWhitespace(label, layoutReference);
        var scale = Math.Min(availableWidth / (double)compacted.Width,
            availableHeight / (double)compacted.Height);
        var width = Math.Max(1, (int)Math.Round(compacted.Width * scale));
        var height = Math.Max(1, (int)Math.Round(compacted.Height * scale));
        var canvas = GrayImage.White(canvasWidth, canvasHeight);
        canvas.Blit(Resize(compacted, width, height, quality), margin + ((availableWidth - width) / 2),
            margin + ((availableHeight - height) / 2));
        return new CompositionResult(canvas, true, warningCode);
    }

    private static CompositionResult Squish(GrayImage label, GrayImage? layoutReference,
        int canvasWidth, int canvasHeight,
        int targetDpi, double marginInches, PrintQualityPreset quality)
    {
        var margin = SafeMargin(targetDpi, marginInches);
        var availableWidth = canvasWidth - (2 * margin);
        var availableHeight = canvasHeight - (2 * margin);
        var compacted = layoutReference is null
            ? ImageAnalysis.CompactVerticalWhitespace(label)
            : ImageAnalysis.CompactVerticalWhitespace(label, layoutReference);
        var canvas = GrayImage.White(canvasWidth, canvasHeight);
        canvas.Blit(Resize(compacted, availableWidth, availableHeight, quality), margin, margin);
        return new CompositionResult(canvas, false, null);
    }

    private static GrayImage Resize(GrayImage image, int width, int height, PrintQualityPreset quality) =>
        quality == PrintQualityPreset.HighQuality
            ? image.ResizeBilinear(width, height)
            : image.Resize(width, height);

    private static int SafeMargin(int targetDpi, double configuredMargin) =>
        Math.Max(1, (int)Math.Ceiling(targetDpi * Math.Max(MinimumSafeMarginInches, configuredMargin)));
}
