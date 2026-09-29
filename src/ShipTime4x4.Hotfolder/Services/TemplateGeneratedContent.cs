using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ShipTime4x4.Core.Raster;
using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.Services;

public static class TemplateGeneratedContent
{
    public static NormalizedRect DefaultDestination(TemplateBuiltInAssetKind asset) => asset switch
    {
        TemplateBuiltInAssetKind.FromDe => new NormalizedRect(.03125, .03125, .205, .055),
        TemplateBuiltInAssetKind.ToA => new NormalizedRect(.26, .03125, .115, .055),
        _ => throw new ArgumentOutOfRangeException(nameof(asset))
    };

    public static GrayImage Render(TemplateZone zone, int dpi)
    {
        if (zone.Type == TemplateZoneType.DividerLine)
            return new GrayImage(32, 4, new byte[128]);
        if (zone.Type != TemplateZoneType.BuiltInAsset || zone.BuiltInAsset == TemplateBuiltInAssetKind.None)
            throw new ArgumentException("The zone is not generated template content.", nameof(zone));

        var text = zone.BuiltInAsset == TemplateBuiltInAssetKind.FromDe ? "FROM / DE" : "TO / A";
        var physicalWidth = zone.BuiltInAsset == TemplateBuiltInAssetKind.FromDe ? .82 : .46;
        var width = Math.Max(48, (int)Math.Round(dpi * physicalWidth));
        var height = Math.Max(20, (int)Math.Round(dpi * .22));
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Arial", height * .57f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.Clear(Color.Black);
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            graphics.DrawString(text, font, Brushes.White, new RectangleF(0, -1, width, height + 1), format);
        }
        return ToGray(bitmap);
    }

    private static GrayImage ToGray(Bitmap bitmap)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var source = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, source, 0, source.Length);
            var output = new byte[bitmap.Width * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var row = data.Stride >= 0 ? y : bitmap.Height - 1 - y;
                var offset = row * Math.Abs(data.Stride) + x * 3;
                output[y * bitmap.Width + x] = (byte)Math.Clamp((int)Math.Round(
                    source[offset + 2] * .299 + source[offset + 1] * .587 + source[offset] * .114), 0, 255);
            }
            return new GrayImage(bitmap.Width, bitmap.Height, output);
        }
        finally { bitmap.UnlockBits(data); }
    }
}
