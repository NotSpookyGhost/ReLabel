using ShipTime4x4.Core.Raster;

namespace ShipTime4x4.Core.Processing;

public enum BlockPriority
{
    ScanCritical,
    Required,
    Flexible,
    Optional
}

public sealed record LayoutBlock(PixelRect Bounds, BlockPriority Priority, int SourceOrder);

public sealed record LabelAnalysis(
    GrayImage NormalizedLabel,
    IReadOnlyList<LayoutBlock> Blocks,
    IReadOnlyList<BarcodeRegion> Barcodes,
    IReadOnlyList<PixelRect> AddressRegions,
    bool IsConfident,
    string? WarningCode);

public sealed class LabelAnalyzer(BarcodeInspector barcodeInspector)
{
    public LabelAnalysis Analyze(GrayImage page, int sourceDpi, IReadOnlyList<PixelRect>? pageAddressRegions = null)
    {
        var padding = Math.Max(1, (int)Math.Round(sourceDpi * 0.02));
        var bounds = ImageAnalysis.FindContentBounds(page, padding: padding);
        if (bounds.IsEmpty)
            throw new LabelAnalysisException("blank-page", "The submitted page contains no printable content.");

        var label = page.Crop(bounds);
        var addressRegions = TranslateRegions(pageAddressRegions ?? [], bounds, label.Width, label.Height);
        return AnalyzeNormalized(label, sourceDpi, addressRegions);
    }

    public LabelAnalysis AnalyzeNormalized(GrayImage label, int sourceDpi,
        IReadOnlyList<PixelRect>? addressRegions = null)
    {
        addressRegions ??= [];
        var padding = Math.Max(1, (int)Math.Round(sourceDpi * 0.02));
        var aspect = label.Width / (double)label.Height;
        if (aspect is < 0.45 or > 0.9 && HasMultipleLabelClusters(label, sourceDpi))
            throw new LabelAnalysisException("multi-label-page", "The submitted page appears to contain more than one label.");
        if (aspect is < 0.45 or > 0.9)
            return new LabelAnalysis(label, [], [], addressRegions, false, "label-bounds-low-confidence");

        var bands = ImageAnalysis.FindHorizontalBands(label, sourceDpi).ToList();
        if (bands.Count < 2)
            return new LabelAnalysis(label, [], [], addressRegions, false, "segmentation-low-confidence");

        var barcodes = barcodeInspector.Detect(label);
        if (barcodes.Count == 0)
            return new LabelAnalysis(label, [], [], addressRegions, false, "barcode-not-detected");

        var criticalBandIndexes = new HashSet<int>();
        foreach (var barcode in barcodes)
        {
            var centerX = barcode.Bounds.X + barcode.Bounds.Width / 2f;
            var centerY = barcode.Bounds.Y + barcode.Bounds.Height / 2f;
            var containing = bands.FindIndex(band => band.Contains(centerX, centerY));
            if (containing >= 0)
                criticalBandIndexes.Add(containing);
        }

        if (criticalBandIndexes.Count == 0)
            return new LabelAnalysis(label, [], barcodes, addressRegions, false, "barcode-grouping-low-confidence");

        var nonCritical = Enumerable.Range(0, bands.Count)
            .Where(index => !criticalBandIndexes.Contains(index))
            .ToArray();
        var requiredIndex = nonCritical.Length == 0
            ? -1
            : nonCritical.OrderByDescending(index => bands[index].Area).First();
        var optionalIndex = nonCritical.Length == 0
            ? -1
            : nonCritical
                .Where(index => index != requiredIndex && index == bands.Count - 1)
                .Where(index => bands[index].Height < label.Height * 0.08)
                .DefaultIfEmpty(-1)
                .First();

        var blocks = new List<LayoutBlock>();
        for (var index = 0; index < bands.Count; index++)
        {
            var priority = criticalBandIndexes.Contains(index)
                ? BlockPriority.ScanCritical
                : index == requiredIndex
                    ? BlockPriority.Required
                    : index == optionalIndex
                        ? BlockPriority.Optional
                        : BlockPriority.Flexible;
            blocks.Add(new LayoutBlock(bands[index].Inflate(padding, padding, label.Width, label.Height), priority, index));
        }

        var covered = blocks.Sum(block => block.Bounds.Area);
        var confident = barcodes.Count == criticalBandIndexes.Count && covered > label.Width * label.Height * 0.12;
        return new LabelAnalysis(label, blocks, barcodes, addressRegions, confident,
            confident ? null : "layout-low-confidence");
    }

    private static IReadOnlyList<PixelRect> TranslateRegions(IReadOnlyList<PixelRect> regions,
        PixelRect labelBounds, int labelWidth, int labelHeight)
    {
        var translated = new List<PixelRect>();
        foreach (var region in regions)
        {
            var left = Math.Max(labelBounds.X, region.X);
            var top = Math.Max(labelBounds.Y, region.Y);
            var right = Math.Min(labelBounds.Right, region.Right);
            var bottom = Math.Min(labelBounds.Bottom, region.Bottom);
            if (right <= left || bottom <= top) continue;
            translated.Add(new PixelRect(left - labelBounds.X, top - labelBounds.Y,
                right - left, bottom - top).Inflate(1, 1, labelWidth, labelHeight));
        }
        return translated;
    }

    private static bool HasMultipleLabelClusters(GrayImage image, int sourceDpi)
    {
        var minimumGap = Math.Max(10, (int)Math.Round(sourceDpi * 0.5));
        var minimumSide = Math.Max(20, (int)Math.Round(sourceDpi * 1.25));
        return HasSeparatingGap(image.Height, minimumGap, minimumSide, index =>
        {
            for (var x = 0; x < image.Width; x++)
                if (image[x, index] < 235)
                    return true;
            return false;
        }) || HasSeparatingGap(image.Width, minimumGap, minimumSide, index =>
        {
            for (var y = 0; y < image.Height; y++)
                if (image[index, y] < 235)
                    return true;
            return false;
        });
    }

    private static bool HasSeparatingGap(int length, int minimumGap, int minimumSide, Func<int, bool> hasInk)
    {
        var gapStart = -1;
        for (var index = 0; index <= length; index++)
        {
            var active = index < length && hasInk(index);
            if (!active && gapStart < 0)
                gapStart = index;
            if (active && gapStart >= 0)
            {
                if (index - gapStart >= minimumGap && gapStart >= minimumSide && length - index >= minimumSide)
                    return true;
                gapStart = -1;
            }
        }
        return false;
    }
}

public sealed class LabelAnalysisException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
