using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using ShipTime4x4.Core.Printing;

namespace ShipTime4x4.Hotfolder.Printing;

/// <summary>Prints already-composed labels at their physical size, through the vendor's driver.</summary>
public sealed class WindowsDriverPrinterClient : IDriverPrinterClient
{
    public void Print(string printerName, string documentName, IReadOnlyList<MonochromePage> pages, int dpi)
    {
        if (dpi is not (203 or 300 or 600) || pages.Count is < 1 or > 500 ||
            pages.Sum(page => (long)page.Data.Length) > MonochromePage.MaximumJobBytes)
            throw new InvalidDataException("Invalid driver print job.");
        using var document = new PrintDocument
        {
            DocumentName = documentName,
            PrintController = new StandardPrintController(),
            OriginAtMargins = false
        };
        var printer = document.PrinterSettings;
        printer.PrinterName = printerName;
        if (!printer.IsValid) throw new InvalidOperationException("The selected Windows printer is unavailable.");
        printer.Copies = 1;
        printer.Collate = false;
        printer.PrintRange = PrintRange.AllPages;
        if (printer.CanDuplex) printer.Duplex = Duplex.Simplex;

        // Preflight ALL pages before starting a spool job, including driver-reported hard margins.
        var settings = new List<PageSettings>(pages.Count);
        foreach (var page in pages)
        {
            var pageSettings = (PageSettings)printer.DefaultPageSettings.Clone();
            pageSettings.Landscape = false;
            pageSettings.Color = false;
            pageSettings.Margins = new Margins(0, 0, 0, 0);
            var width = (int)Math.Round(page.Width * 100d / dpi);
            var height = (int)Math.Round(page.Height * 100d / dpi);
            pageSettings.PaperSize = printer.PaperSizes.Cast<PaperSize>().FirstOrDefault(size =>
                Math.Abs(size.Width - width) <= 1 && Math.Abs(size.Height - height) <= 1)
                ?? new PaperSize("ReLabel label", width, height);
            var resolution = printer.PrinterResolutions.Cast<PrinterResolution>().FirstOrDefault(value =>
                Math.Abs(value.X - dpi) <= dpi * .02 && Math.Abs(value.Y - dpi) <= dpi * .02);
            if (resolution is not null) pageSettings.PrinterResolution = resolution;
            using var measurement = printer.CreateMeasurementGraphics(pageSettings);
            ValidateResolution(measurement.DpiX, measurement.DpiY, dpi);
            ValidateLayout(page, dpi, PhysicalPaperSize(measurement), pageSettings.PrintableArea);
            settings.Add(pageSettings);
        }

        var index = 0;
        document.DefaultPageSettings = settings[0];
        document.QueryPageSettings += (_, args) => args.PageSettings = settings[index];
        document.PrintPage += (_, args) =>
        {
            var page = pages[index];
            var graphics = args.Graphics ?? throw new InvalidOperationException("The printer did not provide a drawing surface.");
            ValidateResolution(graphics.DpiX, graphics.DpiY, dpi);
            ValidateLayout(page, dpi, PhysicalPaperSize(graphics), args.PageSettings.PrintableArea);
            using var bitmap = ToBitmap(page);
            graphics.PageUnit = GraphicsUnit.Inch;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.SmoothingMode = SmoothingMode.None;
            // GDI starts at the printable origin, not the physical paper edge.
            graphics.DrawImage(bitmap, new RectangleF(-args.PageSettings.HardMarginX / 100f,
                -args.PageSettings.HardMarginY / 100f, page.Width / (float)dpi, page.Height / (float)dpi),
                new RectangleF(0, 0, page.Width, page.Height), GraphicsUnit.Pixel);
            args.HasMorePages = ++index < pages.Count;
        };
        document.Print();
    }

    internal static void ValidateResolution(float x, float y, int dpi)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            Math.Abs(x - dpi) > dpi * .02 || Math.Abs(y - dpi) > dpi * .02)
            throw new InvalidOperationException(
                $"The Windows driver is using {x:0} x {y:0} DPI, but ReLabel is set to {dpi} DPI. Match the resolution in ReLabel and Windows Printing preferences.");
    }

    private static Size PhysicalPaperSize(Graphics graphics)
    {
        // PageSettings.Bounds can merely echo the requested custom size even when a driver
        // substitutes its default paper. Query the actual device context before spooling.
        var device = graphics.GetHdc();
        try
        {
            var dpiX = GetDeviceCaps(device, 88); // LOGPIXELSX
            var dpiY = GetDeviceCaps(device, 90); // LOGPIXELSY
            var width = GetDeviceCaps(device, 110); // PHYSICALWIDTH
            var height = GetDeviceCaps(device, 111); // PHYSICALHEIGHT
            if (dpiX <= 0 || dpiY <= 0 || width <= 0 || height <= 0)
                throw new InvalidOperationException("The Windows driver could not report its physical paper size.");
            return new Size((int)Math.Round(width * 100d / dpiX), (int)Math.Round(height * 100d / dpiY));
        }
        finally { graphics.ReleaseHdc(device); }
    }

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr device, int index);

    internal static void ValidateLayout(MonochromePage page, int dpi, Size paperHundredths,
        RectangleF printableHundredths)
    {
        var width = page.Width * 100f / dpi;
        var height = page.Height * 100f / dpi;
        if (Math.Abs(paperHundredths.Width - width) > 2 || Math.Abs(paperHundredths.Height - height) > 2)
            throw new InvalidOperationException("The Windows driver did not accept the label size. Set matching label stock in Windows Printing preferences.");
        if (printableHundredths.Width <= 0 || printableHundredths.Height <= 0)
            throw new InvalidOperationException("The Windows driver reported no printable area.");
        // One-dot tolerance accommodates rounding of driver measurements to hundredths of an inch.
        var tolerance = 100f / dpi;
        var area = RectangleF.Inflate(printableHundredths, tolerance, tolerance);
        for (var y = 0; y < page.Height; y++)
        for (var x = 0; x < page.Width; x++)
        {
            if ((page.Data[y * page.BytesPerRow + x / 8] & (0x80 >> (x % 8))) == 0) continue;
            if (x * 100f / dpi < area.Left || (x + 1) * 100f / dpi > area.Right ||
                y * 100f / dpi < area.Top || (y + 1) * 100f / dpi > area.Bottom)
                throw new InvalidOperationException("Label content falls outside the Windows driver's printable area. Adjust the label layout or driver margins; ReLabel will not shrink or clip it silently.");
        }
    }

    internal static Bitmap ToBitmap(MonochromePage page)
    {
        var bitmap = new Bitmap(page.Width, page.Height, PixelFormat.Format1bppIndexed);
        var palette = bitmap.Palette;
        palette.Entries[0] = Color.White;
        palette.Entries[1] = Color.Black;
        bitmap.Palette = palette;
        var data = bitmap.LockBits(new Rectangle(0, 0, page.Width, page.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
        try
        {
            for (var y = 0; y < page.Height; y++)
                Marshal.Copy(page.Data, y * page.BytesPerRow, data.Scan0 + y * data.Stride, page.BytesPerRow);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
}
