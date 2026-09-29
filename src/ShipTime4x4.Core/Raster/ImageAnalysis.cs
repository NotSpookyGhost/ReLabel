namespace ShipTime4x4.Core.Raster;

public static class ImageAnalysis
{
    public static PixelRect FindContentBounds(GrayImage image, int padding = 0, byte whiteThreshold = 245)
    {
        var left = image.Width;
        var top = image.Height;
        var right = -1;
        var bottom = -1;

        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            if (image[x, y] >= whiteThreshold)
                continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }

        if (right < left || bottom < top)
            return default;

        return new PixelRect(left, top, right - left + 1, bottom - top + 1)
            .Inflate(padding, padding, image.Width, image.Height);
    }

    public static IReadOnlyList<PixelRect> FindHorizontalBands(
        GrayImage image,
        int sourceDpi,
        byte whiteThreshold = 235)
    {
        var active = new bool[image.Height];
        var minimumInk = Math.Max(2, image.Width / 500);
        for (var y = 0; y < image.Height; y++)
        {
            var ink = 0;
            for (var x = 0; x < image.Width; x++)
                if (image[x, y] < whiteThreshold)
                    ink++;
            active[y] = ink >= minimumInk;
        }

        var joinGap = Math.Max(2, (int)Math.Round(sourceDpi * 0.055));
        var bands = new List<(int Start, int End)>();
        var start = -1;
        var lastInk = -1;
        for (var y = 0; y < active.Length; y++)
        {
            if (active[y])
            {
                if (start < 0)
                    start = y;
                lastInk = y;
            }
            else if (start >= 0 && y - lastInk > joinGap)
            {
                bands.Add((start, lastInk));
                start = -1;
            }
        }
        if (start >= 0)
            bands.Add((start, lastInk));

        var result = new List<PixelRect>();
        foreach (var band in bands)
        {
            var left = image.Width;
            var right = -1;
            for (var y = band.Start; y <= band.End; y++)
            for (var x = 0; x < image.Width; x++)
            {
                if (image[x, y] >= whiteThreshold)
                    continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
            if (right >= left)
                result.Add(new PixelRect(left, band.Start, right - left + 1, band.End - band.Start + 1));
        }
        return result;
    }

    public static GrayImage CompactVerticalWhitespace(
        GrayImage image,
        byte whiteThreshold = 242,
        int? maximumBlankRows = null)
        => CompactVerticalWhitespace(image, image, whiteThreshold, maximumBlankRows);

    public static GrayImage CompactVerticalWhitespace(
        GrayImage image,
        GrayImage layoutReference,
        byte whiteThreshold = 242,
        int? maximumBlankRows = null)
    {
        if (image.Width != layoutReference.Width || image.Height != layoutReference.Height)
            throw new ArgumentException("The image and layout reference must have identical dimensions.");
        var minimumInk = Math.Max(3, layoutReference.Width / 350);
        var active = new bool[layoutReference.Height];
        for (var y = 0; y < layoutReference.Height; y++)
        {
            var referenceInk = 0;
            var editedInk = 0;
            for (var x = 0; x < layoutReference.Width &&
                 referenceInk < minimumInk && editedInk < minimumInk; x++)
            {
                if (layoutReference[x, y] < whiteThreshold) referenceInk++;
                if (image[x, y] < whiteThreshold) editedInk++;
            }
            // Keep the stable source layout, but also retain rows newly occupied by an
            // enlarged address. Otherwise fallback compaction can silently discard the edit.
            active[y] = referenceInk >= minimumInk || editedInk >= minimumInk;
        }

        var first = Array.FindIndex(active, value => value);
        var last = Array.FindLastIndex(active, value => value);
        if (first < 0 || last <= first)
            return image;

        var maxGap = maximumBlankRows ?? Math.Max(4, (int)Math.Round(image.Width * 0.025));
        var rows = new List<int>(last - first + 1);
        var yPosition = first;
        while (yPosition <= last)
        {
            if (active[yPosition])
            {
                rows.Add(yPosition++);
                continue;
            }

            var gapStart = yPosition;
            while (yPosition <= last && !active[yPosition])
                yPosition++;
            var gapLength = yPosition - gapStart;
            var keep = Math.Min(gapLength, maxGap);
            var offset = (gapLength - keep) / 2;
            for (var row = gapStart + offset; row < gapStart + offset + keep; row++)
                rows.Add(row);
        }

        if (rows.Count >= last - first + 1)
            return image.Crop(new PixelRect(0, first, image.Width, last - first + 1));

        var output = new byte[checked(image.Width * rows.Count)];
        for (var destinationRow = 0; destinationRow < rows.Count; destinationRow++)
            Buffer.BlockCopy(image.Pixels, rows[destinationRow] * image.Width, output,
                destinationRow * image.Width, image.Width);
        return new GrayImage(image.Width, rows.Count, output);
    }

    public static byte OtsuThreshold(GrayImage image)
    {
        var histogram = new long[256];
        foreach (var value in image.Pixels)
            histogram[value]++;

        var total = image.Pixels.LongLength;
        long sum = 0;
        for (var i = 0; i < 256; i++)
            sum += i * histogram[i];

        long backgroundWeight = 0;
        long backgroundSum = 0;
        double bestVariance = -1;
        byte best = 127;
        for (var threshold = 0; threshold < 256; threshold++)
        {
            backgroundWeight += histogram[threshold];
            if (backgroundWeight == 0)
                continue;
            var foregroundWeight = total - backgroundWeight;
            if (foregroundWeight == 0)
                break;
            backgroundSum += threshold * histogram[threshold];
            var backgroundMean = backgroundSum / (double)backgroundWeight;
            var foregroundMean = (sum - backgroundSum) / (double)foregroundWeight;
            var variance = backgroundWeight * (double)foregroundWeight * Math.Pow(backgroundMean - foregroundMean, 2);
            if (variance > bestVariance)
            {
                bestVariance = variance;
                best = (byte)threshold;
            }
        }
        return (byte)Math.Clamp((int)best, 96, 224);
    }
}
