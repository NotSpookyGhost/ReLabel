using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ShipTime4x4.Core.Printing;
using ShipTime4x4.Core.Processing;
using ShipTime4x4.Core.Raster;
using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.Printing;
using ShipTime4x4.Hotfolder.Services;

namespace ShipTime4x4.Tests;

public sealed class PrinterOutputTests
{
    [Fact]
    public void OldSettingsKeepZplAndNativeOverridesDefaultOff()
    {
        var settings = JsonSerializer.Deserialize<HotfolderConfiguration>("{}")!;
        Assert.Equal(PrinterOutputMode.Zpl, settings.PrinterOutput);
        Assert.True(settings.NativeUsePrinterDefaults);
        settings.Validate();
    }

    [Theory]
    [InlineData(PrinterOutputMode.Zpl)]
    [InlineData(PrinterOutputMode.Epl2)]
    [InlineData(PrinterOutputMode.Tspl)]
    [InlineData(PrinterOutputMode.WindowsDriver)]
    public void SettingsRoundTrip(PrinterOutputMode mode)
    {
        var expected = new HotfolderConfiguration
        {
            PrinterOutput = mode, NativeUsePrinterDefaults = false,
            NativeMedia = LabelMediaMode.BlackMark, NativeGapMillimeters = 2.5,
            EplDensity = 13, EplSpeed = 3, TsplDensity = 12, TsplSpeedIps = 3.5
        };
        var actual = JsonSerializer.Deserialize<HotfolderConfiguration>(JsonSerializer.Serialize(expected))!;
        actual.Validate();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BitmapUsesBlackMsbFirstAndWhitePadding()
    {
        var page = MonochromePage.FromImage(new GrayImage(9, 2,
            [0,255,255,255,255,255,255,0,0, 255,0,255,255,255,255,255,255,255]));
        Assert.Equal(new byte[] { 0x81, 0x80, 0x40, 0 }, page.Data);
        using var bitmap = WindowsDriverPrinterClient.ToBitmap(page);
        Assert.Equal(Color.Black.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.White.ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), bitmap.GetPixel(8, 0).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), bitmap.GetPixel(1, 1).ToArgb());
    }

    [Fact]
    public void PackedRasterMatchesTheExistingZplThresholdAndBitOrder()
    {
        var image = new GrayImage(9, 2, [0,30,70,100,140,190,230,255,0, 255,240,200,150,90,50,10,0,255]);
        var packed = MonochromePage.FromImage(image);
        var zpl = Encoding.ASCII.GetString(new ShipTime4x4.Core.Zpl.ZplGraphicEncoder()
            .Encode(image, ImageAnalysis.OtsuThreshold(image)));
        Assert.Contains(Convert.ToHexString(packed.Data) + "^FS", zpl);
    }

    [Theory]
    [InlineData(PrinterOutputMode.Epl2, "GW0,0,1,4,", "\nP1\n")]
    [InlineData(PrinterOutputMode.Tspl, "BITMAP 0,0,1,4,0,", "\r\nPRINT 1,1\r\n")]
    public void BinaryDataAndPageOrderSurviveEncoding(PrinterOutputMode mode, string marker, string trailer)
    {
        var first = new MonochromePage(8, 4, [0xff, 0xf5, 0xf2, 0]); // wire NUL, LF, CR, FF
        var second = new MonochromePage(8, 4, [0x80, 0x40, 0x20, 0x10]);
        var bytes = NativeRasterEncoder.Encode(mode, [first, second], new());
        var text = Encoding.Latin1.GetString(bytes);
        var firstStart = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var secondStart = text.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length;
        Assert.Equal(new byte[] { 0, 10, 13, 255 }, bytes[firstStart..(firstStart + 4)]);
        Assert.Equal(new byte[] { 0x7f, 0xbf, 0xdf, 0xef }, bytes[secondStart..(secondStart + 4)]);
        Assert.True(firstStart < secondStart);
        Assert.EndsWith(trailer, text);
        Assert.DoesNotContain("~SD", text);
        Assert.DoesNotContain("^XA", text);
        Assert.DoesNotContain("DENSITY", text);
        Assert.DoesNotContain("SPEED", text);
        Assert.DoesNotContain("\nD8\n", text);
        Assert.DoesNotContain("\nS2\n", text);
    }

    [Theory]
    [InlineData(PrinterOutputMode.Epl2, LabelMediaMode.Gap, "Q812,24")]
    [InlineData(PrinterOutputMode.Epl2, LabelMediaMode.BlackMark, "Q812,B24")]
    [InlineData(PrinterOutputMode.Epl2, LabelMediaMode.Continuous, "Q812,0")]
    [InlineData(PrinterOutputMode.Tspl, LabelMediaMode.Gap, "GAP 3 mm,0 mm")]
    [InlineData(PrinterOutputMode.Tspl, LabelMediaMode.BlackMark, "BLINE 3 mm,0 mm")]
    [InlineData(PrinterOutputMode.Tspl, LabelMediaMode.Continuous, "GAP 0 mm,0 mm")]
    public void MediaCommandsMatchTheSelectedStock(PrinterOutputMode mode, LabelMediaMode media, string command)
    {
        var bytes = NativeRasterEncoder.Encode(mode, [MonochromePage.FromImage(GrayImage.White(812, 812))],
            new NativePrinterOptions(Media: media));
        Assert.Contains(command, Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void NativeOverridesAreNotZebraDarknessValuesAndUseInvariantNumbers()
    {
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-CA");
            var pages = new[] { MonochromePage.FromImage(GrayImage.White(812, 1218)) };
            var tspl = Encoding.ASCII.GetString(NativeRasterEncoder.Encode(PrinterOutputMode.Tspl, pages,
                new NativePrinterOptions(GapMillimeters: 2.5, Density: 12, Speed: 3.5)));
            Assert.Contains("SIZE 101.6 mm,152.4 mm", tspl);
            Assert.Contains("GAP 2.5 mm,0 mm", tspl);
            Assert.Contains("DENSITY 12\r\nSPEED 3.5", tspl);
            var epl = Encoding.ASCII.GetString(NativeRasterEncoder.Encode(PrinterOutputMode.Epl2, pages,
                new NativePrinterOptions(Density: 9, Speed: 2)));
            Assert.Contains("\nD9\nS2\nGW", epl);
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
    }

    [Theory]
    [InlineData(PrinterOutputMode.Zpl)]
    [InlineData(PrinterOutputMode.Epl2)]
    [InlineData(PrinterOutputMode.Tspl)]
    [InlineData(PrinterOutputMode.WindowsDriver)]
    public void RoutesOnlyToTheChosenBackendAndRetainsAllPages(PrinterOutputMode mode)
    {
        var raw = new FakeRaw();
        var driver = new FakeDriver();
        var printer = new LabelPrinter(raw, driver);
        var first = new PreparedLabel(new GrayImage(8, 1, [0,255,255,255,255,255,255,255]), [1,2], false, null);
        var second = new PreparedLabel(new GrayImage(8, 1, [255,0,255,255,255,255,255,255]), [3,4], false, null);
        var prepared = PdfLabelProcessor.CombinePages([first, second]);
        printer.Print(new HotfolderConfiguration { PrinterOutput = mode, PhysicalPrinterQueue = "test" }, "job", prepared);
        if (mode == PrinterOutputMode.WindowsDriver)
        {
            Assert.Null(raw.Data);
            Assert.Equal(2, driver.Pages!.Count);
            Assert.Equal(0x80, driver.Pages[0].Data[0]);
            Assert.Equal(0x40, driver.Pages[1].Data[0]);
        }
        else
        {
            Assert.Null(driver.Pages);
            Assert.NotNull(raw.Data);
            if (mode == PrinterOutputMode.Zpl) Assert.Equal(new byte[] { 1,2,3,4 }, raw.Data);
        }
    }

    [Fact]
    public void NeverPrintsOnlyFirstPageOfAnIncompleteMultiPageCache()
    {
        var prepared = new PreparedLabel(GrayImage.White(8, 1), [1], false, null, PageCount: 2);
        Assert.Throws<InvalidDataException>(() => prepared.GetPrintPages());
    }

    [Fact]
    public void RejectsInvalidSettingsAndNativeBounds()
    {
        Assert.Throws<InvalidOperationException>(() => new HotfolderConfiguration
            { PrinterOutput = (PrinterOutputMode)99 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new HotfolderConfiguration
            { PrinterOutput = PrinterOutputMode.Epl2, PrintResolutionDpi = 600 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new HotfolderConfiguration
            { TsplSpeedIps = double.NaN }.Validate());
        Assert.Throws<ArgumentException>(() => new MonochromePage(24000, 24000, []));
        Assert.Throws<InvalidDataException>(() =>
            NativeRasterEncoder.Encode(PrinterOutputMode.Tspl, [], new()));
        Assert.Throws<ArgumentException>(() =>
            NativeRasterEncoder.Encode(PrinterOutputMode.Tspl, [], new(Density: 30)));
    }

    [Fact]
    public void DriverRejectsSizeMismatchAndClippingButAcceptsBlankMargins()
    {
        var image = GrayImage.White(812, 812);
        image[100, 100] = 0;
        var area = new RectangleF(12.5f, 12.5f, 375, 375);
        WindowsDriverPrinterClient.ValidateLayout(MonochromePage.FromImage(image), 203, new Size(400, 400), area);
        Assert.Throws<InvalidOperationException>(() =>
            WindowsDriverPrinterClient.ValidateLayout(MonochromePage.FromImage(image), 203, new Size(400, 600), area));
        image[0, 0] = 0;
        Assert.Throws<InvalidOperationException>(() =>
            WindowsDriverPrinterClient.ValidateLayout(MonochromePage.FromImage(image), 203, new Size(400, 400), area));
        WindowsDriverPrinterClient.ValidateResolution(200, 200, 203);
        Assert.Throws<InvalidOperationException>(() => WindowsDriverPrinterClient.ValidateResolution(300, 300, 203));
        Assert.Throws<InvalidOperationException>(() => WindowsDriverPrinterClient.ValidateResolution(300, 600, 300));
    }

    [Fact]
    public void CalibrationContainsDecodableBarcodes()
    {
        var label = CalibrationLabelBuilder.Create(203);
        var payloads = new BarcodeInspector().Detect(label.Image).Select(region => region.Value);
        Assert.Contains("123456789012", payloads);
        Assert.Contains("RELABEL-QUALITY-TEST", payloads);
    }

    private sealed class FakeRaw : IRawPrinterClient
    {
        public byte[]? Data;
        public void Print(string printerName, string documentName, ReadOnlySpan<byte> data) => Data = data.ToArray();
    }
    private sealed class FakeDriver : IDriverPrinterClient
    {
        public IReadOnlyList<MonochromePage>? Pages;
        public void Print(string printerName, string documentName, IReadOnlyList<MonochromePage> pages, int dpi) => Pages = pages;
    }
}
