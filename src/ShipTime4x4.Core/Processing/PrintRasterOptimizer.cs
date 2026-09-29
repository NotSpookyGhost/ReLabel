using ShipTime4x4.Core.Raster;

namespace ShipTime4x4.Core.Processing;

public static class PrintRasterOptimizer
{
    public static GrayImage Apply(GrayImage image, PrintQualityPreset quality, BarcodeInspector barcodeInspector)
    {
        if (quality != PrintQualityPreset.HighQuality)
            return image;

        var sharpened = SharpenForText(image);
        var globalThreshold = ImageAnalysis.OtsuThreshold(sharpened);
        var decodedBarcodes = barcodeInspector.Detect(image)
            .Select(result => result.Bounds.Inflate(Math.Max(2, image.Width / 200),
                Math.Max(2, image.Height / 16), image.Width, image.Height))
            .Where(bounds => !bounds.IsEmpty)
            .ToArray();
        var barcodes = MergeRegions(decodedBarcodes.Concat(FindBarcodePatterns(image)).ToArray());
        var localThresholds = barcodes.Select(bounds => ImageAnalysis.OtsuThreshold(image.Crop(bounds))).ToArray();
        var pixels = new byte[image.Pixels.Length];

        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var threshold = globalThreshold;
            var value = sharpened[x, y];
            for (var index = 0; index < barcodes.Count; index++)
            {
                if (!barcodes[index].Contains(x, y)) continue;
                threshold = localThresholds[index];
                value = image[x, y];
                break;
            }
            pixels[(y * image.Width) + x] = value < threshold ? (byte)0 : (byte)255;
        }

        var optimized = new GrayImage(image.Width, image.Height, pixels);
        return barcodes.Count == 0 || barcodeInspector.ContainsSamePayloads(image, optimized)
            ? optimized
            : image;
    }

    private static GrayImage SharpenForText(GrayImage image)
    {
        var output = (byte[])image.Pixels.Clone();
        for (var y = 1; y < image.Height - 1; y++)
        for (var x = 1; x < image.Width - 1; x++)
        {
            var value = (image[x, y] * 5) - image[x - 1, y] - image[x + 1, y] - image[x, y - 1] - image[x, y + 1];
            output[(y * image.Width) + x] = (byte)Math.Clamp(value, 0, 255);
        }
        return new GrayImage(image.Width, image.Height, output);
    }

    private static IReadOnlyList<PixelRect> FindBarcodePatterns(GrayImage image)
    {
        var threshold = ImageAnalysis.OtsuThreshold(image);
        var horizontal = FindBands(image.Height, index => RowSignature(image, index, threshold),
            (start, end) => BoundsForRows(image, start, end, threshold), image.Width, image.Height,
            image.Width, image.Height);
        var vertical = FindBands(image.Width, index => ColumnSignature(image, index, threshold),
            (start, end) => BoundsForColumns(image, start, end, threshold), image.Height, image.Width,
            image.Width, image.Height);
        return horizontal.Concat(vertical).ToArray();
    }

    private static IReadOnlyList<PixelRect> FindBands(int length, Func<int, bool> qualifies,
        Func<int, int, PixelRect> bounds, int longSide, int shortSide, int imageWidth, int imageHeight)
    {
        var result = new List<PixelRect>();
        var start = -1;
        var last = -1;
        var joinGap = Math.Max(1, shortSide / 300);
        for (var index = 0; index < length; index++)
        {
            if (qualifies(index))
            {
                if (start < 0) start = index;
                last = index;
            }
            else if (start >= 0 && index - last > joinGap)
            {
                AddBand(start, last);
                start = -1;
            }
        }
        if (start >= 0) AddBand(start, last);
        return result;

        void AddBand(int first, int final)
        {
            if (final - first + 1 < Math.Max(10, shortSide / 35)) return;
            var region = bounds(first, final);
            if (!region.IsEmpty && Math.Max(region.Width, region.Height) >= longSide / 4)
                result.Add(region.Inflate(Math.Max(3, imageWidth / 160),
                    Math.Max(3, imageHeight / 80), imageWidth, imageHeight));
        }
    }

    private static bool RowSignature(GrayImage image, int y, byte threshold)
    {
        var ink = 0;
        var transitions = 0;
        var previous = image[0, y] < threshold;
        for (var x = 0; x < image.Width; x++)
        {
            var black = image[x, y] < threshold;
            if (black) ink++;
            if (x > 0 && black != previous) transitions++;
            previous = black;
        }
        return ink >= image.Width / 10 && transitions >= Math.Max(24, image.Width / 18);
    }

    private static bool ColumnSignature(GrayImage image, int x, byte threshold)
    {
        var ink = 0;
        var transitions = 0;
        var previous = image[x, 0] < threshold;
        for (var y = 0; y < image.Height; y++)
        {
            var black = image[x, y] < threshold;
            if (black) ink++;
            if (y > 0 && black != previous) transitions++;
            previous = black;
        }
        return ink >= image.Height / 10 && transitions >= Math.Max(24, image.Height / 18);
    }

    private static PixelRect BoundsForRows(GrayImage image, int top, int bottom, byte threshold)
    {
        var left = image.Width;
        var right = -1;
        for (var y = top; y <= bottom; y++)
        for (var x = 0; x < image.Width; x++)
        {
            if (image[x, y] >= threshold) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
        }
        return right < left ? default : new PixelRect(left, top, right - left + 1, bottom - top + 1);
    }

    private static PixelRect BoundsForColumns(GrayImage image, int left, int right, byte threshold)
    {
        var top = image.Height;
        var bottom = -1;
        for (var x = left; x <= right; x++)
        for (var y = 0; y < image.Height; y++)
        {
            if (image[x, y] >= threshold) continue;
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        return bottom < top ? default : new PixelRect(left, top, right - left + 1, bottom - top + 1);
    }

    private static IReadOnlyList<PixelRect> MergeRegions(IReadOnlyList<PixelRect> regions)
    {
        var merged = new List<PixelRect>();
        foreach (var region in regions.Where(region => !region.IsEmpty).OrderBy(region => region.Y))
        {
            var index = merged.FindIndex(existing => Overlaps(existing, region));
            if (index < 0)
            {
                merged.Add(region);
                continue;
            }
            var existing = merged[index];
            var left = Math.Min(existing.X, region.X);
            var top = Math.Min(existing.Y, region.Y);
            var right = Math.Max(existing.Right, region.Right);
            var bottom = Math.Max(existing.Bottom, region.Bottom);
            merged[index] = new PixelRect(left, top, right - left, bottom - top);
        }
        return merged;
    }

    private static bool Overlaps(PixelRect first, PixelRect second) =>
        first.X < second.Right && second.X < first.Right && first.Y < second.Bottom && second.Y < first.Bottom;
}
