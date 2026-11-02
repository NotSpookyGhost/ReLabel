using System.Drawing.Imaging;
using System.Text;
using PDFtoImage;
using ShipTime4x4.Core.Raster;
using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.Services;

internal sealed class PdfWorkerClient(string applicationFolder)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _executable = Path.Combine(applicationFolder, "ReLabel.exe");
    private const int Protocol = 1;
    private const int MaximumTextBytes = 4 * 1024 * 1024;

    public int Count(string path, PdfSafetyLimits limits) => Execute(path, 0, 300, 0, limits).PageCount;
    public GrayImage Render(string path, int dpi, int page, PdfSafetyLimits limits)
        => Execute(path, 2, dpi, page, limits).SourcePage;
    public PdfInspection Inspect(string path, PdfSafetyLimits limits)
    {
        var inspection = Execute(path, 1, 300, 0, limits);
        // Only bounded raw pixels cross the process boundary. Do not decode an image
        // supplied by a potentially compromised native renderer in the parent.
        using var bitmap = GrayImageBitmapConverter.ToBitmap(inspection.SourcePage);
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return inspection with { SourcePng = png.ToArray() };
    }

    private PdfInspection Execute(string path, int operation, int dpi, int page, PdfSafetyLimits limits)
    {
        Gate.Wait();
        try
        {
            var pdf = limits.ReadPdf(path);
            using var process = new RestrictedPdfProcess(_executable);
            var task = Task.Run(() =>
            {
                using var writer = new BinaryWriter(process.Input, Encoding.UTF8, leaveOpen: true);
                writer.Write(Protocol);
                writer.Write(operation);
                writer.Write(dpi);
                writer.Write(page);
                writer.Write(limits.MaximumFileMegabytes);
                writer.Write(limits.MaximumPages);
                writer.Write(limits.MaximumPageMegapixels);
                writer.Write(limits.WorkerTimeoutSeconds);
                writer.Write(pdf.Length);
                writer.Write(pdf);
                writer.Flush();
                using var reader = new BinaryReader(process.Output, Encoding.UTF8, leaveOpen: true);
                if (!reader.ReadBoolean()) throw new InvalidDataException(ReadText(reader, 4096));
                var count = reader.ReadInt32();
                limits.ValidatePageCount(count);
                if (operation == 0) return new PdfInspection(GrayImage.White(1, 1), [], "", "", 0, count);
                var width = reader.ReadInt32();
                var height = reader.ReadInt32();
                if (width < 1 || height < 1 || width > 24000 || height > 24000 ||
                    (long)width * height > limits.MaximumPagePixels)
                    throw new InvalidDataException("PDF worker returned invalid image dimensions.");
                var pixels = new byte[checked(width * height)];
                reader.BaseStream.ReadExactly(pixels);
                var text = ReadText(reader, MaximumTextBytes);
                var tsv = ReadText(reader, MaximumTextBytes);
                var confidence = reader.ReadSingle();
                if (!float.IsFinite(confidence)) throw new InvalidDataException("PDF worker returned invalid confidence.");
                return new PdfInspection(new GrayImage(width, height, pixels), [], text, tsv, confidence, count);
            });
            try { return task.WaitAsync(TimeSpan.FromSeconds(limits.WorkerTimeoutSeconds)).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            {
                throw new InvalidDataException($"PDF processing exceeded {limits.WorkerTimeoutSeconds} seconds and was stopped.");
            }
            catch (IOException ex)
            {
                throw new InvalidDataException("The PDF worker stopped unexpectedly or exceeded its memory limit.", ex);
            }
            finally
            {
                process.Dispose();
                _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        finally { Gate.Release(); }
    }

    internal static int RunWorker()
    {
        using var reader = new BinaryReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var writer = new BinaryWriter(Console.OpenStandardOutput(), Encoding.UTF8);
        try
        {
            if (!RestrictedPdfProcess.IsLowIntegrity())
                throw new InvalidDataException("PDF processing requires the restricted worker.");
            if (reader.ReadInt32() != Protocol) throw new InvalidDataException("Invalid PDF worker protocol.");
            var operation = reader.ReadInt32();
            var dpi = reader.ReadInt32();
            var page = reader.ReadInt32();
            if (operation is < 0 or > 2 || dpi is not (203 or 300 or 600))
                throw new InvalidDataException("Invalid PDF worker operation.");
            var limits = new PdfSafetyLimits
            {
                MaximumFileMegabytes = reader.ReadInt32(), MaximumPages = reader.ReadInt32(),
                MaximumPageMegapixels = reader.ReadInt32(), WorkerTimeoutSeconds = reader.ReadInt32()
            };
            limits.Validate();
            var length = reader.ReadInt32();
            if (length < 5 || length > limits.MaximumFileBytes) throw new InvalidDataException("PDF input exceeds its size limit.");
            var pdf = new byte[length];
            reader.BaseStream.ReadExactly(pdf);
            var count = Conversion.GetPageCount(pdf);
            limits.ValidatePageCount(count);
            PdfLabelProcessor.ValidatePageSizes(pdf, count, dpi, limits);
            PdfInspection? inspection = operation switch
            {
                1 => new PdfLabelProcessor(AppContext.BaseDirectory).InspectBytesInWorker(pdf, limits),
                2 => new PdfInspection(PdfLabelProcessor.RenderBytesInWorker(pdf, dpi, page, limits), [], "", "", 0, count),
                _ => null
            };
            if (inspection is not null &&
                (Encoding.UTF8.GetByteCount(inspection.RecognizedText) > MaximumTextBytes ||
                 Encoding.UTF8.GetByteCount(inspection.RecognizedTsv) > MaximumTextBytes))
                throw new InvalidDataException("PDF text exceeds its processing limit.");
            writer.Write(true);
            writer.Write(count);
            if (inspection is not null)
            {
                writer.Write(inspection.SourcePage.Width);
                writer.Write(inspection.SourcePage.Height);
                writer.Write(inspection.SourcePage.Pixels);
                WriteText(writer, inspection.RecognizedText);
                WriteText(writer, inspection.RecognizedTsv);
                writer.Write(inspection.OcrConfidence);
            }
            writer.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                writer.Write(false);
                WriteText(writer, ex is InvalidDataException
                    ? ex.Message[..Math.Min(1000, ex.Message.Length)]
                    : "The PDF could not be processed safely. It may be damaged or unsupported.");
                writer.Flush();
            }
            catch (IOException) { }
            return 1;
        }
    }

    private static void WriteText(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    internal static string ReadText(BinaryReader reader, int maximum)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum) throw new InvalidDataException("PDF worker text exceeds its size limit.");
        var bytes = new byte[count];
        reader.BaseStream.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }
}
