using ShipTime4x4.Core.Raster;
using ZXing;
using ZXing.Common;
using ZXing.Multi;

namespace ShipTime4x4.Core.Processing;

public sealed record BarcodeRegion(string Value, BarcodeFormat Format, PixelRect Bounds);

public sealed class BarcodeInspector
{
    private static readonly IDictionary<DecodeHintType, object> Hints =
        new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.ALSO_INVERTED] = true
        };

    public IReadOnlyList<BarcodeRegion> Detect(GrayImage image)
    {
        var source = new RGBLuminanceSource(
            image.Pixels,
            image.Width,
            image.Height,
            RGBLuminanceSource.BitmapFormat.Gray8);
        var bitmap = new BinaryBitmap(new HybridBinarizer(source));
        var baseReader = new MultiFormatReader();
        Result[] results;
        try
        {
            results = new GenericMultipleBarcodeReader(baseReader).decodeMultiple(bitmap, Hints) ?? [];
        }
        catch (ReaderException)
        {
            results = [];
        }

        if (results.Length == 0)
        {
            try
            {
                var result = baseReader.decode(bitmap, Hints);
                if (result is not null)
                    results = [result];
            }
            catch (ReaderException)
            {
                // A label with no decodable barcode is handled by the fallback path.
            }
        }

        return results
            .Where(result => !string.IsNullOrWhiteSpace(result.Text))
            .GroupBy(result => (result.BarcodeFormat, result.Text))
            .Select(group => ToRegion(group.OrderByDescending(result =>
                result.ResultPoints?.Count(point => point is not null) ?? 0).First(), image))
            .ToArray();
    }

    public bool ContainsSamePayloads(GrayImage source, GrayImage output)
    {
        var expected = Detect(source).Select(x => (x.Format, x.Value)).ToHashSet();
        if (expected.Count == 0)
            return false;
        var actual = Detect(output).Select(x => (x.Format, x.Value)).ToHashSet();
        return expected.SetEquals(actual);
    }

    public IReadOnlySet<(BarcodeFormat Format, string Value)> PayloadsInRegions(
        GrayImage source, IReadOnlyList<PixelRect> regions)
    {
        var payloads = new HashSet<(BarcodeFormat, string)>();
        foreach (var barcode in Detect(source))
        {
            var centerX = barcode.Bounds.X + barcode.Bounds.Width / 2f;
            var centerY = barcode.Bounds.Y + barcode.Bounds.Height / 2f;
            if (regions.Any(region => region.Contains(centerX, centerY) ||
                                      IntersectionRatio(region, barcode.Bounds) >= .20))
                payloads.Add((barcode.Format, barcode.Value));
        }
        // A detector can miss a barcode on a busy full page but decode the isolated zone.
        // Inflate the crop slightly to preserve as much quiet area as the page permits.
        foreach (var region in regions)
        {
            var expanded = region.Inflate(Math.Max(4, source.Width / 200),
                Math.Max(4, source.Height / 200), source.Width, source.Height);
            foreach (var barcode in Detect(source.Crop(expanded)))
                payloads.Add((barcode.Format, barcode.Value));
        }
        return payloads;
    }

    public bool OutputContainsPayloads(GrayImage output,
        IReadOnlySet<(BarcodeFormat Format, string Value)> expected,
        IReadOnlyList<PixelRect>? barcodeRegions = null)
    {
        if (expected.Count == 0) return false;
        var actual = Detect(output).Select(x => (x.Format, x.Value)).ToHashSet();
        if (barcodeRegions is not null)
            actual.UnionWith(PayloadsInRegions(output, barcodeRegions));
        return expected.IsSubsetOf(actual);
    }

    private static double IntersectionRatio(PixelRect first, PixelRect second)
    {
        var width = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.X, second.X));
        var height = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Y, second.Y));
        return width * height / (double)Math.Max(1, second.Area);
    }

    internal static BarcodeRegion ToRegion(Result result, GrayImage image)
    {
        var imageWidth = image.Width;
        var imageHeight = image.Height;
        // Some decoders return a populated result-point array with null entries.
        // Keep the valid geometry instead of failing preparation for the entire label.
        var points = (result.ResultPoints ?? []).Where(point => point is not null).ToArray();
        if (points.Length == 0)
            return new BarcodeRegion(result.Text, result.BarcodeFormat, new PixelRect(0, 0, imageWidth, imageHeight));

        var minX = Math.Clamp((int)Math.Floor(points.Min(p => p.X)), 0, imageWidth - 1);
        var minY = Math.Clamp((int)Math.Floor(points.Min(p => p.Y)), 0, imageHeight - 1);
        var maxX = Math.Clamp((int)Math.Ceiling(points.Max(p => p.X)), minX + 1, imageWidth);
        var maxY = Math.Clamp((int)Math.Ceiling(points.Max(p => p.Y)), minY + 1, imageHeight);
        var raw = new PixelRect(minX, minY, maxX - minX, maxY - minY);
        var bounds = IsOneDimensional(result.BarcodeFormat)
            ? ExpandOneDimensionalBounds(image, raw, points)
            : raw.Inflate(Math.Max(2, imageWidth / 100), Math.Max(2, imageHeight / 100), imageWidth, imageHeight);
        return new BarcodeRegion(result.Text, result.BarcodeFormat, bounds);
    }

    private static PixelRect ExpandOneDimensionalBounds(GrayImage image, PixelRect raw,
        IReadOnlyList<ResultPoint> points)
    {
        var horizontalPadding = Math.Max(3, image.Width / 100);
        var left = Math.Max(0, raw.X - horizontalPadding);
        var right = Math.Min(image.Width, raw.Right + horizontalPadding);
        var width = Math.Max(1, right - left);
        var pointY = points.Count == 0 ? raw.Y + raw.Height / 2 :
            (int)Math.Round(points.Average(point => point.Y));
        pointY = Math.Clamp(pointY, 0, image.Height - 1);

        var searchRadius = Math.Max(18, image.Height / 10);
        var seed = -1;
        for (var distance = 0; distance <= searchRadius && seed < 0; distance++)
        {
            var above = pointY - distance;
            var below = pointY + distance;
            if (above >= 0 && LooksLikeOneDimensionalBarcodeRow(image, left, right, above)) seed = above;
            else if (below < image.Height && LooksLikeOneDimensionalBarcodeRow(image, left, right, below)) seed = below;
        }
        if (seed < 0)
            return raw.Inflate(horizontalPadding, Math.Max(3, image.Height / 100), image.Width, image.Height);

        var top = ExpandBarcodeRows(image, left, right, seed, -1);
        var bottom = ExpandBarcodeRows(image, left, right, seed, 1);
        var verticalPadding = Math.Max(4, image.Height / 100);
        var humanReadableAllowance = Math.Max(6, image.Height / 35);
        top = Math.Max(0, top - verticalPadding);
        bottom = Math.Min(image.Height - 1, bottom + verticalPadding + humanReadableAllowance);
        return new PixelRect(left, top, width, Math.Max(1, bottom - top + 1));
    }

    private static int ExpandBarcodeRows(GrayImage image, int left, int right, int seed, int direction)
    {
        var lastGood = seed;
        var misses = 0;
        for (var y = seed + direction; y >= 0 && y < image.Height; y += direction)
        {
            if (LooksLikeOneDimensionalBarcodeRow(image, left, right, y))
            {
                lastGood = y;
                misses = 0;
            }
            else if (++misses > 2) break;
        }
        return lastGood;
    }

    private static bool LooksLikeOneDimensionalBarcodeRow(GrayImage image, int left, int right, int y)
    {
        var width = Math.Max(1, right - left);
        var dark = 0;
        var transitions = 0;
        var previousDark = image[left, y] < 170;
        for (var x = left; x < right; x++)
        {
            var currentDark = image[x, y] < 170;
            if (currentDark) dark++;
            if (x > left && currentDark != previousDark) transitions++;
            previousDark = currentDark;
        }
        return dark >= width * .10 && dark <= width * .92 &&
            transitions >= Math.Max(8, width / 45);
    }

    private static bool IsOneDimensional(BarcodeFormat format) => format is
        BarcodeFormat.CODABAR or BarcodeFormat.CODE_39 or BarcodeFormat.CODE_93 or
        BarcodeFormat.CODE_128 or BarcodeFormat.EAN_8 or BarcodeFormat.EAN_13 or
        BarcodeFormat.ITF or BarcodeFormat.RSS_14 or BarcodeFormat.RSS_EXPANDED or
        BarcodeFormat.UPC_A or BarcodeFormat.UPC_E or BarcodeFormat.UPC_EAN_EXTENSION;
}
