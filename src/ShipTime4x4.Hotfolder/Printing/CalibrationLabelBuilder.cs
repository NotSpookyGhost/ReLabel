using ShipTime4x4.Core.Printing;
using ShipTime4x4.Core.Processing;
using ShipTime4x4.Core.Raster;
using ZXing;

namespace ShipTime4x4.Hotfolder.Printing;

internal static class CalibrationLabelBuilder
{
    public static PreparedLabel Create(int dpi)
    {
        using var bitmap = new Bitmap(dpi * 4, dpi * 4);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            using var title = new Font("Segoe UI", dpi * .20f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", dpi * .12f, FontStyle.Regular, GraphicsUnit.Pixel);
            graphics.DrawString("ReLabel Quality Test", title, Brushes.Black, dpi * .2f, dpi * .2f);
            graphics.DrawString($"{dpi} DPI - 4 x 4 inches", small, Brushes.Black, dpi * .2f, dpi * .55f);
            var writer = new MultiFormatWriter();
            var barcode = writer.encode("123456789012", BarcodeFormat.CODE_128,
                (int)(dpi * 3.4), (int)(dpi * .65));
            DrawCode(graphics, barcode, (int)(dpi * .3), (int)(dpi * 1.0));
            graphics.DrawString("123456789012", small, Brushes.Black, dpi * .3f, dpi * 1.7f);
            var qr = writer.encode("RELABEL-QUALITY-TEST", BarcodeFormat.QR_CODE, dpi, dpi);
            DrawCode(graphics, qr, (int)(dpi * .2), dpi * 2);
            graphics.DrawString("1 / 2 / 3 dot lines", small, Brushes.Black, dpi * 1.7f, dpi * 2.1f);
            for (var thickness = 1; thickness <= 3; thickness++)
            {
                graphics.FillRectangle(Brushes.Black, dpi * 1.7f, dpi * (2.25f + thickness * .16f),
                    dpi * 1.9f, thickness);
                graphics.FillRectangle(Brushes.Black, dpi * (2.2f + thickness * .3f), dpi * 3.05f,
                    thickness, dpi * .55f);
            }
        }
        var image = GrayImage.White(bitmap.Width, bitmap.Height);
        var locked = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Math.Abs(locked.Stride)];
            for (var y = 0; y < bitmap.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(locked.Scan0 + y * locked.Stride, row, 0, row.Length);
                for (var x = 0; x < bitmap.Width; x++) image[x, y] = row[x * 4 + 2];
            }
        }
        finally { bitmap.UnlockBits(locked); }
        return new PreparedLabel(image, [], false, null)
            { PrintPages = [MonochromePage.FromImage(image)] };
    }

    private static void DrawCode(Graphics graphics, ZXing.Common.BitMatrix matrix, int left, int top)
    {
        for (var y = 0; y < matrix.Height; y++)
        for (var x = 0; x < matrix.Width; x++)
            if (matrix[x, y]) graphics.FillRectangle(Brushes.Black, left + x, top + y, 1, 1);
    }
}
