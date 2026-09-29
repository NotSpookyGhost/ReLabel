using ShipTime4x4.Core.Raster;
using ZXing;

namespace ShipTime4x4.Core.Processing;

public enum RotatedBarcodeCompensation
{
    Off = 0,
    OneDot = 1,
    TwoDots = 2,
    ThreeDots = 3,
    FourDots = 4,
    FiveDots = 5
}

public enum TextEnhancement
{
    Off = 0,
    Larger = 1,
    ExtraLarge = 2,
    Custom = 3
}

public sealed record BarcodeCompensationResult(GrayImage Image, int AppliedDots);

public static class LabelPrintEnhancements
{
    public static GrayImage EnlargeAddressText(GrayImage image, IReadOnlyList<PixelRect> addressRegions,
        TextEnhancement enhancement, int customFromScalePercent = 100, int customToScalePercent = 100)
    {
        if (enhancement == TextEnhancement.Off || addressRegions.Count == 0)
            return image;

        var regions = addressRegions.Where(region => !region.IsEmpty)
            .OrderBy(region => region.X).ToArray();
        if (regions.Length == 0)
            return image;
        ValidateCustomScale(customFromScalePercent, 450, nameof(customFromScalePercent));
        ValidateCustomScale(customToScalePercent, 250, nameof(customToScalePercent));
        // Custom scales are intentionally independent. Process each column against the
        // current label so a constrained TO block cannot force the FROM block down (or vice versa).
        if (enhancement == TextEnhancement.Custom && regions.Length > 1)
        {
            var output = image;
            for (var index = 0; index < regions.Length; index++)
            {
                var scale = index == 0 ? customFromScalePercent : customToScalePercent;
                // A single-region pass uses the first scale slot regardless of which source
                // column the rectangle represents in the full label.
                output = EnlargeAddressText(output, [regions[index]], TextEnhancement.Custom, scale, 100);
            }
            return output;
        }
        // Extra Large deliberately uses more of the verified blank area below each address.
        // Horizontal and vertical scale are independent because shipping labels commonly
        // reserve considerably more height than width around the FROM/TO blocks.
        var gap = Math.Max(4, image.Width / 160);
        var divider = regions.Length >= 2 ? FindDivider(image, regions[0], regions[1]) : null;
        if (divider is not null)
        {
            regions[0] = TrimRight(regions[0], divider.Value.X - gap);
            regions[1] = TrimLeft(regions[1], divider.Value.Right + gap);
        }
        regions = regions.Where(region => !region.IsEmpty).ToArray();
        var sources = regions.Select(region => CreateAddressSource(image, region))
            .Where(source => source is not null).Cast<AddressSource>().ToArray();
        if (sources.Length == 0) return image;
        var requestedHorizontalScales = sources.Select((_, index) => enhancement switch
        {
            TextEnhancement.Custom => (index == 0 ? customFromScalePercent : customToScalePercent) / 100d,
            TextEnhancement.ExtraLarge => 1.28,
            _ => 1.15
        }).ToArray();
        var requestedVerticalScales = sources.Select((_, index) => enhancement switch
        {
            TextEnhancement.Custom => (index == 0 ? customFromScalePercent : customToScalePercent) / 100d,
            TextEnhancement.ExtraLarge => 1.58,
            _ => 1.20
        }).ToArray();

        for (var progress = 1d; progress >= 0.02; progress -= 0.01)
        {
            var horizontalScales = requestedHorizontalScales.Select(scale => 1 + ((scale - 1) * progress)).ToArray();
            var verticalScales = requestedVerticalScales.Select(scale => 1 + ((scale - 1) * progress)).ToArray();
            var destinations = BuildDestinations(image, sources, divider, horizontalScales, verticalScales, gap);
            if (destinations is null)
                continue;
            var rendered = destinations.Select(item => new RenderedAddress(item.Source, item.Destination,
                item.Source.Content.ResizeBilinear(item.Destination.Width, item.Destination.Height))).ToArray();
            if (rendered.Any(item => !PreservesAllInk(item.Source.Content, item.Image)) ||
                HasCollision(image, sources, divider, rendered)) continue;

            var output = new GrayImage(image.Width, image.Height, (byte[])image.Pixels.Clone());
            foreach (var source in sources) FillWhite(output, source.ClearBounds);
            if (divider is not null) FillWhite(output, divider.Value);
            foreach (var item in rendered) BlitInk(output, item.Image, item.Destination.X, item.Destination.Y);

            if (divider is not null)
            {
                var dividerShift = destinations.Count >= 2
                    ? destinations[1].Destination.X - sources[1].ClearBounds.X
                    : 0;
                var moved = divider.Value with { X = divider.Value.X + dividerShift };
                if (moved.Right <= output.Width)
                    output.Blit(image.Crop(divider.Value), moved.X, moved.Y);
            }
            return output;
        }
        // Tight layouts may have vertical room but no horizontal reserve. Keep every original
        // address column at full width and use the compacted inter-line whitespace to enlarge text height.
        for (var progress = 1d; progress >= 0.02; progress -= 0.01)
        {
            var horizontalScales = Enumerable.Repeat(1d, sources.Length).ToArray();
            var verticalScales = requestedVerticalScales.Select(scale => 1 + ((scale - 1) * progress)).ToArray();
            var destinations = BuildDestinations(image, sources, divider, horizontalScales, verticalScales, gap);
            if (destinations is null) continue;
            var rendered = destinations.Select(item => new RenderedAddress(item.Source, item.Destination,
                item.Source.Content.ResizeBilinear(item.Destination.Width, item.Destination.Height))).ToArray();
            if (rendered.Any(item => !PreservesAllInk(item.Source.Content, item.Image)) ||
                HasCollision(image, sources, divider, rendered)) continue;

            var output = new GrayImage(image.Width, image.Height, (byte[])image.Pixels.Clone());
            foreach (var source in sources) FillWhite(output, source.ClearBounds);
            foreach (var item in rendered) BlitInk(output, item.Image, item.Destination.X, item.Destination.Y);
            return output;
        }
        // With no room outside either column, reclaim only blank inter-line rows inside each
        // original address rectangle. Every ink-bearing pixel remains represented and nothing
        // outside the FROM/TO rectangles can be overwritten.
        var inPlace = sources.Select((source, index) =>
        {
            var height = Math.Min(source.ClearBounds.Height,
                Math.Max(source.Content.Height,
                    (int)Math.Round(source.Content.Height * requestedVerticalScales[index])));
            return new AddressDestination(source, new PixelRect(source.ClearBounds.X,
                source.ClearBounds.Y, source.Content.Width, height));
        }).ToArray();
        if (inPlace.Any(item => item.Destination.Height > item.Source.Content.Height))
        {
            var output = new GrayImage(image.Width, image.Height, (byte[])image.Pixels.Clone());
            foreach (var source in sources) FillWhite(output, source.ClearBounds);
            foreach (var item in inPlace)
            {
                var rendered = item.Source.Content.ResizeBilinear(
                    item.Destination.Width, item.Destination.Height);
                if (!PreservesAllInk(item.Source.Content, rendered)) return image;
                BlitInk(output, rendered, item.Destination.X, item.Destination.Y);
            }
            return output;
        }
        return image;
    }

    private static void ValidateCustomScale(int scalePercent, int maximum, string parameterName)
    {
        if (scalePercent < 100 || scalePercent > maximum)
            throw new ArgumentOutOfRangeException(parameterName,
                $"Custom address scale must be between 100% and {maximum}%.");
    }

    private static AddressSource? CreateAddressSource(GrayImage image, PixelRect region)
    {
        var localBounds = ImageAnalysis.FindContentBounds(image.Crop(region), whiteThreshold: 248);
        if (localBounds.IsEmpty) return null;
        var clearBounds = new PixelRect(region.X + localBounds.X, region.Y + localBounds.Y,
            localBounds.Width, localBounds.Height);
        var content = CompactLosslessBlankRows(image.Crop(clearBounds));
        return new AddressSource(clearBounds, content);
    }

    private static GrayImage CompactLosslessBlankRows(GrayImage image)
    {
        var active = new bool[image.Height];
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            if (image[x, y] < 248)
            {
                active[y] = true;
                break;
            }
        var first = Array.FindIndex(active, value => value);
        var last = Array.FindLastIndex(active, value => value);
        if (first < 0 || last < first) return image;

        var maximumGap = Math.Max(4, image.Height / 45);
        var rows = new List<int>();
        for (var y = first; y <= last;)
        {
            if (active[y])
            {
                rows.Add(y++);
                continue;
            }
            var start = y;
            while (y <= last && !active[y]) y++;
            var length = y - start;
            var keep = Math.Min(length, maximumGap);
            var offset = (length - keep) / 2;
            for (var row = start + offset; row < start + offset + keep; row++) rows.Add(row);
        }
        if (rows.Count == image.Height) return image;
        var pixels = new byte[checked(image.Width * rows.Count)];
        for (var destinationY = 0; destinationY < rows.Count; destinationY++)
            Buffer.BlockCopy(image.Pixels, rows[destinationY] * image.Width, pixels,
                destinationY * image.Width, image.Width);
        return new GrayImage(image.Width, rows.Count, pixels);
    }

    private static IReadOnlyList<AddressDestination>? BuildDestinations(GrayImage image,
        IReadOnlyList<AddressSource> sources, PixelRect? divider, IReadOnlyList<double> horizontalScales,
        IReadOnlyList<double> verticalScales, int gap)
    {
        var result = new List<AddressDestination>(sources.Count);
        var desiredFirstWidth = Math.Max(1, (int)Math.Round(sources[0].Content.Width * horizontalScales[0]));
        var firstHeight = Math.Max(1, (int)Math.Round(sources[0].Content.Height * verticalScales[0]));

        if (sources.Count >= 2)
        {
            var oldDividerX = divider?.X ?? ((sources[0].ClearBounds.Right + sources[1].ClearBounds.X) / 2);
            var dividerWidth = divider?.Width ?? 1;
            var maximumShift = image.Width - gap - sources[1].Content.Width - sources[1].ClearBounds.X;
            var requestedDividerShift = Math.Max(gap,
                desiredFirstWidth - sources[0].Content.Width);
            var initialShift = Math.Max(0, Math.Min(requestedDividerShift, maximumShift));
            var newDividerX = oldDividerX + initialShift;
            var maximumFirstWidth = newDividerX - gap - sources[0].ClearBounds.X;
            var firstWidth = Math.Min(desiredFirstWidth, maximumFirstWidth);
            if (firstWidth < sources[0].Content.Width) return null;
            result.Add(new AddressDestination(sources[0], new PixelRect(sources[0].ClearBounds.X,
                sources[0].ClearBounds.Y, firstWidth, firstHeight)));

            newDividerX = Math.Max(newDividerX, result[0].Destination.Right + gap);
            var shift = newDividerX - oldDividerX;
            var desiredWidth = Math.Max(1, (int)Math.Round(sources[1].Content.Width * horizontalScales[1]));
            var height = Math.Max(1, (int)Math.Round(sources[1].Content.Height * verticalScales[1]));
            var minimumX = newDividerX + dividerWidth + gap;
            var x = Math.Max(sources[1].ClearBounds.X + shift, minimumX);
            var width = Math.Min(desiredWidth, image.Width - gap - x);
            if (width < sources[1].Content.Width) return null;
            result.Add(new AddressDestination(sources[1],
                new PixelRect(x, sources[1].ClearBounds.Y, width, height)));
        }
        else
        {
            var width = Math.Min(desiredFirstWidth, image.Width - gap - sources[0].ClearBounds.X);
            if (width < sources[0].Content.Width) return null;
            result.Add(new AddressDestination(sources[0], new PixelRect(sources[0].ClearBounds.X,
                sources[0].ClearBounds.Y, width, firstHeight)));
        }

        for (var index = 2; index < sources.Count; index++)
        {
            var width = Math.Max(1, (int)Math.Round(sources[index].Content.Width * horizontalScales[index]));
            var height = Math.Max(1, (int)Math.Round(sources[index].Content.Height * verticalScales[index]));
            result.Add(new AddressDestination(sources[index],
                new PixelRect(sources[index].ClearBounds.X, sources[index].ClearBounds.Y, width, height)));
        }
        return result.Any(item => item.Destination.Right > image.Width - gap ||
                                  item.Destination.Bottom > image.Height - gap)
            ? null
            : result;
    }

    private static bool HasCollision(GrayImage image, IReadOnlyList<AddressSource> sources, PixelRect? divider,
        IReadOnlyList<RenderedAddress> destinations)
    {
        foreach (var destination in destinations)
        for (var y = 0; y < destination.Image.Height; y++)
        for (var x = 0; x < destination.Image.Width; x++)
        {
            if (destination.Image[x, y] >= 248) continue;
            var targetX = destination.Destination.X + x;
            var targetY = destination.Destination.Y + y;
            if (image[targetX, targetY] >= 235 ||
                sources.Any(source => source.ClearBounds.Contains(targetX, targetY)) ||
                divider?.Contains(targetX, targetY) == true)
                continue;
            return true;
        }
        return false;
    }

    public static bool PreservesAllInk(GrayImage source, GrayImage resized, byte inkThreshold = 220)
    {
        for (var sourceY = 0; sourceY < source.Height; sourceY++)
        for (var sourceX = 0; sourceX < source.Width; sourceX++)
        {
            if (source[sourceX, sourceY] >= inkThreshold) continue;
            var left = Math.Clamp((int)Math.Floor(sourceX * resized.Width / (double)source.Width),
                0, resized.Width - 1);
            var right = Math.Clamp((int)Math.Ceiling((sourceX + 1) * resized.Width / (double)source.Width),
                left + 1, resized.Width);
            var top = Math.Clamp((int)Math.Floor(sourceY * resized.Height / (double)source.Height),
                0, resized.Height - 1);
            var bottom = Math.Clamp((int)Math.Ceiling((sourceY + 1) * resized.Height / (double)source.Height),
                top + 1, resized.Height);
            var preserved = false;
            for (var y = top; y < bottom && !preserved; y++)
            for (var x = left; x < right; x++)
                if (resized[x, y] < 248)
                {
                    preserved = true;
                    break;
                }
            if (!preserved) return false;
        }
        return true;
    }

    private static PixelRect? FindDivider(GrayImage image, PixelRect from, PixelRect to)
    {
        var searchLeft = Math.Max(from.X, from.Right - Math.Max(4, from.Width / 12));
        var searchRight = Math.Min(image.Width, to.X + Math.Max(2, to.Width / 40));
        var maximumAddressHeight = Math.Max(from.Height, to.Height);
        var searchTop = Math.Max(0, Math.Min(from.Y, to.Y) - Math.Max(4, maximumAddressHeight / 8));
        var scoreBottom = Math.Min(image.Height,
            Math.Max(from.Bottom, to.Bottom) + Math.Max(8, maximumAddressHeight / 3));
        var traceBottom = Math.Min(image.Height,
            Math.Max(from.Bottom, to.Bottom) + Math.Max(12, (int)Math.Ceiling(maximumAddressHeight * 1.25)));
        if (searchRight <= searchLeft || scoreBottom <= searchTop) return null;

        var bestX = -1;
        var bestInk = 0;
        for (var x = searchLeft; x < searchRight; x++)
        {
            var ink = 0;
            for (var y = searchTop; y < scoreBottom; y++)
                if (image[x, y] < 100) ink++;
            if (ink <= bestInk) continue;
            bestInk = ink;
            bestX = x;
        }
        if (bestX < 0 || bestInk < Math.Max(12, (scoreBottom - searchTop) / 4)) return null;
        var run = FindDividerRun(image, bestX, searchLeft, searchRight, searchTop, traceBottom,
            Math.Min(from.Y, to.Y), Math.Max(from.Bottom, to.Bottom));
        if (run is null) return null;
        var (top, bottom) = run.Value;
        var left = bestX;
        var right = bestX + 1;
        while (left > searchLeft && ColumnIsDivider(image, left - 1, top, bottom)) left--;
        while (right < searchRight && ColumnIsDivider(image, right, top, bottom)) right++;
        return new PixelRect(left, top, right - left, bottom - top + 1);
    }

    private static (int Top, int Bottom)? FindDividerRun(GrayImage image, int x, int searchLeft,
        int searchRight, int searchTop, int searchBottom, int addressTop, int addressBottom)
    {
        var runs = new List<(int Top, int Bottom)>();
        var start = -1;
        var lastInk = -1;
        for (var y = searchTop; y < searchBottom; y++)
        {
            var hasInk = false;
            for (var currentX = Math.Max(searchLeft, x - 1); currentX < Math.Min(searchRight, x + 2); currentX++)
                hasInk |= image[currentX, y] < 100;
            if (hasInk)
            {
                if (start < 0) start = y;
                lastInk = y;
            }
            else if (start >= 0 && y - lastInk > 2)
            {
                runs.Add((start, lastInk));
                start = -1;
            }
        }
        if (start >= 0) runs.Add((start, lastInk));
        return runs.Where(run => run.Bottom >= addressTop && run.Top <= addressBottom)
            .OrderByDescending(run => run.Bottom - run.Top).Cast<(int Top, int Bottom)?>().FirstOrDefault();
    }

    private static bool ColumnIsDivider(GrayImage image, int x, int top, int bottom)
    {
        var ink = 0;
        for (var y = top; y <= bottom; y++) if (image[x, y] < 100) ink++;
        return ink >= Math.Max(8, (bottom - top + 1) / 3);
    }

    private static PixelRect TrimRight(PixelRect region, int right) =>
        new(region.X, region.Y, Math.Max(0, Math.Min(region.Right, right) - region.X), region.Height);

    private static PixelRect TrimLeft(PixelRect region, int left)
    {
        var x = Math.Max(region.X, left);
        return new PixelRect(x, region.Y, Math.Max(0, region.Right - x), region.Height);
    }

    private static void BlitInk(GrayImage destination, GrayImage source, int x, int y)
    {
        for (var sourceY = 0; sourceY < source.Height; sourceY++)
        for (var sourceX = 0; sourceX < source.Width; sourceX++)
            if (source[sourceX, sourceY] < 248)
                destination[x + sourceX, y + sourceY] = source[sourceX, sourceY];
    }

    private sealed record AddressSource(PixelRect ClearBounds, GrayImage Content);
    private sealed record AddressDestination(AddressSource Source, PixelRect Destination);
    private sealed record RenderedAddress(AddressSource Source, PixelRect Destination, GrayImage Image);

    public static GrayImage CompensateRotatedBarcodes(GrayImage image, LabelRotation rotation,
        RotatedBarcodeCompensation compensation, BarcodeInspector barcodeInspector)
        => CompensateRotatedBarcodesWithResult(image, rotation, compensation, barcodeInspector).Image;

    public static BarcodeCompensationResult CompensateRotatedBarcodesWithResult(GrayImage image,
        LabelRotation rotation, RotatedBarcodeCompensation compensation, BarcodeInspector barcodeInspector)
    {
        if (rotation is not (LabelRotation.Clockwise90 or LabelRotation.Clockwise270) ||
            compensation == RotatedBarcodeCompensation.Off)
            return new BarcodeCompensationResult(image, 0);

        var barcodes = barcodeInspector.Detect(image)
            .Where(barcode => IsOneDimensional(barcode.Format))
            .ToArray();
        if (barcodes.Length == 0)
            return new BarcodeCompensationResult(image, 0);

        var requestedDots = (int)compensation;
        for (var dots = requestedDots; dots >= 1; dots--)
        {
            var candidate = new GrayImage(image.Width, image.Height, (byte[])image.Pixels.Clone());
            foreach (var barcode in barcodes)
                RetractFeedDirectionTrailingEdges(candidate, ProtectBarcode(barcode.Bounds, image), dots);
            if (barcodeInspector.ContainsSamePayloads(image, candidate))
                return new BarcodeCompensationResult(candidate, dots);
        }
        return new BarcodeCompensationResult(image, 0);
    }

    public static void RetractFeedDirectionTrailingEdges(GrayImage image, PixelRect region, int dots)
    {
        if (dots is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(dots));
        var threshold = ImageAnalysis.OtsuThreshold(image.Crop(region));
        for (var pass = 0; pass < dots; pass++)
        {
            var source = (byte[])image.Pixels.Clone();
            for (var y = region.Y; y < region.Bottom - 1; y++)
            {
                if (!LooksLikeBarcodeBarRow(source, image.Width, region, y, threshold))
                    continue;
            for (var x = region.X; x < region.Right; x++)
            {
                var index = (y * image.Width) + x;
                var next = index + image.Width;
                // Never erase the last native printhead row of a bar. Wider bars can accept
                // stronger correction while the narrowest valid bars remain present.
                var previous = index - image.Width;
                if (y > region.Y && source[index] < threshold && source[previous] < threshold &&
                    source[next] >= threshold)
                    image.Pixels[index] = 255;
            }
            }
        }
    }

    private static bool LooksLikeBarcodeBarRow(byte[] pixels, int imageWidth, PixelRect region,
        int y, byte threshold)
    {
        var longestRun = 0;
        var currentRun = 0;
        for (var x = region.X; x < region.Right; x++)
        {
            if (pixels[(y * imageWidth) + x] < threshold)
            {
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }
        return longestRun >= Math.Max(8, region.Width / 4);
    }

    private static PixelRect ProtectBarcode(PixelRect bounds, GrayImage image)
    {
        var shortSideExpansion = Math.Max(8, Math.Min(image.Width, image.Height) / 10);
        var normalExpansion = Math.Max(3, Math.Min(image.Width, image.Height) / 100);
        return bounds.Width >= bounds.Height
            ? bounds.Inflate(normalExpansion, shortSideExpansion, image.Width, image.Height)
            : bounds.Inflate(shortSideExpansion, normalExpansion, image.Width, image.Height);
    }

    private static bool IsOneDimensional(BarcodeFormat format) => format is not (
        BarcodeFormat.QR_CODE or BarcodeFormat.DATA_MATRIX or BarcodeFormat.PDF_417 or
        BarcodeFormat.AZTEC or BarcodeFormat.MAXICODE);

    private static void FillWhite(GrayImage image, PixelRect region)
    {
        for (var y = region.Y; y < region.Bottom; y++)
            Array.Fill(image.Pixels, (byte)255, (y * image.Width) + region.X, region.Width);
    }
}
