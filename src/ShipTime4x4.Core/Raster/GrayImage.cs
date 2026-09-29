namespace ShipTime4x4.Core.Raster;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public int Area => Math.Max(0, Width) * Math.Max(0, Height);

    public PixelRect Inflate(int x, int y, int maxWidth, int maxHeight)
    {
        var left = Math.Max(0, X - x);
        var top = Math.Max(0, Y - y);
        var right = Math.Min(maxWidth, Right + x);
        var bottom = Math.Min(maxHeight, Bottom + y);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    public bool Contains(float x, float y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public sealed class GrayImage
{
    public GrayImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height))
            throw new ArgumentException("Pixel buffer dimensions are invalid.");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public static GrayImage White(int width, int height) =>
        new(width, height, Enumerable.Repeat((byte)255, checked(width * height)).ToArray());

    public byte this[int x, int y]
    {
        get => Pixels[(y * Width) + x];
        set => Pixels[(y * Width) + x] = value;
    }

    public GrayImage Crop(PixelRect rectangle)
    {
        if (rectangle.IsEmpty || rectangle.X < 0 || rectangle.Y < 0 ||
            rectangle.Right > Width || rectangle.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(rectangle));

        var output = new byte[checked(rectangle.Width * rectangle.Height)];
        for (var y = 0; y < rectangle.Height; y++)
            Buffer.BlockCopy(Pixels, ((rectangle.Y + y) * Width + rectangle.X), output, y * rectangle.Width, rectangle.Width);
        return new GrayImage(rectangle.Width, rectangle.Height, output);
    }

    public GrayImage Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        var output = new byte[checked(width * height)];
        var xRatio = (double)Width / width;
        var yRatio = (double)Height / height;
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(Height - 1, (int)((y + 0.5) * yRatio));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(Width - 1, (int)((x + 0.5) * xRatio));
                output[(y * width) + x] = this[sourceX, sourceY];
            }
        }
        return new GrayImage(width, height, output);
    }

    public GrayImage ResizeBilinear(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (width == Width && height == Height)
            return new GrayImage(Width, Height, (byte[])Pixels.Clone());

        var output = new byte[checked(width * height)];
        var xScale = Width / (double)width;
        var yScale = Height / (double)height;
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Clamp(((y + 0.5) * yScale) - 0.5, 0, Height - 1);
            var y0 = (int)Math.Floor(sourceY);
            var y1 = Math.Min(Height - 1, y0 + 1);
            var fy = sourceY - y0;
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Clamp(((x + 0.5) * xScale) - 0.5, 0, Width - 1);
                var x0 = (int)Math.Floor(sourceX);
                var x1 = Math.Min(Width - 1, x0 + 1);
                var fx = sourceX - x0;
                var top = this[x0, y0] + ((this[x1, y0] - this[x0, y0]) * fx);
                var bottom = this[x0, y1] + ((this[x1, y1] - this[x0, y1]) * fx);
                output[(y * width) + x] = (byte)Math.Clamp((int)Math.Round(top + ((bottom - top) * fy)), 0, 255);
            }
        }
        return new GrayImage(width, height, output);
    }

    public void Blit(GrayImage source, int x, int y)
    {
        if (x < 0 || y < 0 || x + source.Width > Width || y + source.Height > Height)
            throw new ArgumentOutOfRangeException(nameof(source));
        for (var row = 0; row < source.Height; row++)
            Buffer.BlockCopy(source.Pixels, row * source.Width, Pixels, ((y + row) * Width) + x, source.Width);
    }

    public GrayImage RotateClockwise(int degrees)
    {
        if (degrees is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(degrees), "Rotation must be 0, 90, 180, or 270 degrees.");
        if (degrees == 0)
            return new GrayImage(Width, Height, (byte[])Pixels.Clone());

        var outputWidth = degrees is 90 or 270 ? Height : Width;
        var outputHeight = degrees is 90 or 270 ? Width : Height;
        var output = White(outputWidth, outputHeight);
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            switch (degrees)
            {
                case 90:
                    output[Height - 1 - y, x] = this[x, y];
                    break;
                case 180:
                    output[Width - 1 - x, Height - 1 - y] = this[x, y];
                    break;
                case 270:
                    output[y, Width - 1 - x] = this[x, y];
                    break;
            }
        }
        return output;
    }
}
