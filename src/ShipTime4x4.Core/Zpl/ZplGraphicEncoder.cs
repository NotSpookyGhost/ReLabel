using System.Globalization;
using System.Text;
using ShipTime4x4.Core.Raster;

namespace ShipTime4x4.Core.Zpl;

public sealed class ZplGraphicEncoder
{
    public const int MaximumGraphicBytes = 99_999;

    public byte[] Encode(GrayImage image, byte threshold = 180, double darkness = 10, double speedIps = 4)
    {
        if (darkness is < 0 or > 30)
            throw new ArgumentOutOfRangeException(nameof(darkness), "Darkness must be between 0 and 30.");
        if (speedIps is < 1 or > 14)
            throw new ArgumentOutOfRangeException(nameof(speedIps), "Print speed is outside the supported range.");
        var bytesPerRow = (image.Width + 7) / 8;
        var maximumRowsPerBand = Math.Max(1, MaximumGraphicBytes / bytesPerRow);
        var builder = new StringBuilder(checked(image.Width * image.Height / 3));
        builder.Append("~SD").Append(darkness.ToString("0.0", CultureInfo.InvariantCulture))
            .Append("\n^XA\n^PR").Append(speedIps.ToString("0.#", CultureInfo.InvariantCulture))
            .Append("\n^PW").Append(image.Width.ToString(CultureInfo.InvariantCulture))
            .Append("\n^LL").Append(image.Height.ToString(CultureInfo.InvariantCulture)).Append('\n');

        for (var startY = 0; startY < image.Height; startY += maximumRowsPerBand)
        {
            var rows = Math.Min(maximumRowsPerBand, image.Height - startY);
            var byteCount = checked(rows * bytesPerRow);
            builder.Append("^FO0,").Append(startY.ToString(CultureInfo.InvariantCulture))
                .Append("^GFA,").Append(byteCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(byteCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(bytesPerRow.ToString(CultureInfo.InvariantCulture)).Append(',');

            for (var y = startY; y < startY + rows; y++)
            {
                for (var byteIndex = 0; byteIndex < bytesPerRow; byteIndex++)
                {
                    byte packed = 0;
                    for (var bit = 0; bit < 8; bit++)
                    {
                        var x = (byteIndex * 8) + bit;
                        if (x < image.Width && image[x, y] < threshold)
                            packed |= (byte)(0x80 >> bit);
                    }
                    builder.Append(packed.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
            builder.Append("^FS\n");
        }

        builder.Append("^XZ\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
