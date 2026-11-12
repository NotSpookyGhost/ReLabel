using ShipTime4x4.Core.Raster;

namespace ShipTime4x4.Core.Printing;

/// <summary>Device-independent, row-major bitmap. MSB first; a set bit is black.</summary>
public sealed class MonochromePage
{
    public const long MaximumJobBytes = 128L * 1024 * 1024;
    public int Width { get; }
    public int Height { get; }
    public int BytesPerRow => (Width + 7) / 8;
    public byte[] Data { get; }

    public MonochromePage(int width, int height, byte[] data)
    {
        if (width <= 0 || height <= 0 || width > 24000 || height > 24000 ||
            (long)width * height > 40_000_000 || data.Length != ((width + 7) / 8) * height)
            throw new ArgumentException("Invalid monochrome page dimensions.");
        Width = width;
        Height = height;
        Data = data;
        // Padding bits must never become a printed strip at the right edge.
        if (width % 8 != 0)
            for (var y = 0; y < height; y++)
                Data[(y + 1) * BytesPerRow - 1] &= (byte)(0xff << (8 - width % 8));
    }

    public static MonochromePage FromImage(GrayImage image)
    {
        var threshold = ImageAnalysis.OtsuThreshold(image);
        var stride = (image.Width + 7) / 8;
        var bytes = new byte[checked(stride * image.Height)];
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            if (image[x, y] < threshold)
                bytes[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
        return new MonochromePage(image.Width, image.Height, bytes);
    }
}
