using System.Text;
using ShipTime4x4.Core.Processing;
using ShipTime4x4.Core.Raster;
using ShipTime4x4.Hotfolder.Services;

namespace ShipTime4x4.Tests;

public sealed class MultiPagePdfTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "ReLabel-multipage-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CombinePagesPreservesPrintOrderAndPageWarnings()
    {
        var first = new PreparedLabel(GrayImage.White(2, 2), Encoding.ASCII.GetBytes("^XA^FD1^FS^XZ"),
            false, null, 1);
        var second = new PreparedLabel(GrayImage.White(2, 2), Encoding.ASCII.GetBytes("^XA^FD2^FS^XZ"),
            true, "fallback-test", 3);

        var combined = PdfLabelProcessor.CombinePages([first, second]);

        Assert.Equal(2, combined.PageCount);
        Assert.Equal("^XA^FD1^FS^XZ^XA^FD2^FS^XZ", Encoding.ASCII.GetString(combined.Zpl));
        Assert.True(combined.UsedFallback);
        Assert.Equal("page-2:fallback-test", combined.WarningCode);
        Assert.Equal(3, combined.AppliedBarcodeCompensationDots);
        Assert.Same(first.Image, combined.Image);
    }

    [Fact]
    public void TwoPagePdfInspectsAndPreparesBothLabels()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "two-label-shipment.pdf");
        WritePdf(path, 2);
        var processor = new PdfLabelProcessor(AppContext.BaseDirectory);

        var inspection = processor.Inspect(path);
        var prepared = processor.Prepare(path, 203, .125, 4, 4, LabelFitMode.Proportional,
            LabelRotation.None, PrintQualityPreset.Fast, 10, 4,
            RotatedBarcodeCompensation.Off, TextEnhancement.Off);
        var zpl = Encoding.ASCII.GetString(prepared.Zpl);

        Assert.Equal(2, inspection.PageCount);
        Assert.Equal(2, prepared.PageCount);
        Assert.Equal(2, Count(zpl, "^XA"));
        Assert.Equal(2, Count(zpl, "^XZ"));
        Assert.True(zpl.IndexOf("^XA", StringComparison.Ordinal) <
                    zpl.LastIndexOf("^XA", StringComparison.Ordinal));
    }

    private static int Count(string value, string token)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0;
             index += token.Length)
            count++;
        return count;
    }

    private static void WritePdf(string path, int pageCount)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>"
        };
        var pageIds = Enumerable.Range(0, pageCount).Select(index => 3 + index * 2).ToArray();
        objects.Add($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageCount} >>");
        for (var index = 0; index < pageCount; index++)
        {
            var contentId = pageIds[index] + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 288 432] /Resources << >> /Contents {contentId} 0 R >>");
            var y = 330 - index * 12;
            var stream = $"0 g 2 w 12 12 264 408 re S 20 {y} 248 45 re f 20 180 248 75 re f 40 70 208 55 re f";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream");
        }

        using var output = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            output.Write(bytes);
        }
        Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(output.Position);
            Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, output.ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
}
