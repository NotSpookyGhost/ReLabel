namespace ShipTime4x4.Hotfolder.Models;

public sealed record PdfSafetyLimits
{
    public int MaximumFileMegabytes { get; init; } = 50;
    public int MaximumPages { get; init; } = 100;
    public int MaximumPageMegapixels { get; init; } = 40;
    public int WorkerTimeoutSeconds { get; init; } = 45;
    public const int MaximumJobBytes = 128 * 1024 * 1024;
    public long MaximumFileBytes => MaximumFileMegabytes * 1024L * 1024;
    public long MaximumPagePixels => MaximumPageMegapixels * 1_000_000L;

    public void Validate()
    {
        if (MaximumFileMegabytes is < 1 or > 100 || MaximumPages is < 1 or > 500 ||
            MaximumPageMegapixels is < 5 or > 40 || WorkerTimeoutSeconds is < 5 or > 120)
            throw new InvalidOperationException("PDF limits must be 1-100 MB, 1-500 pages, 5-40 megapixels, and 5-120 seconds.");
    }

    public void ValidatePageCount(int count)
    {
        if (count < 1 || count > MaximumPages)
            throw new InvalidDataException($"PDF rejected: maximum {MaximumPages} pages per file.");
    }

    public void ValidatePageSize(double pointsWide, double pointsHigh, int dpi)
    {
        var width = Math.Ceiling(pointsWide / 72 * dpi);
        var height = Math.Ceiling(pointsHigh / 72 * dpi);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 ||
            width > 24000 || height > 24000 || width * height > MaximumPagePixels)
            throw new InvalidDataException($"PDF rejected: a page exceeds the {MaximumPageMegapixels}-megapixel rendering limit.");
    }

    public byte[] ReadPdf(string path)
    {
        Validate();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("PDF links are not accepted. Copy the actual PDF into Incoming.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 5 || stream.Length > MaximumFileBytes)
            throw new InvalidDataException($"PDF rejected: maximum file size is {MaximumFileMegabytes} MB.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("PDF changed while it was being read.");
        if (System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(1024, bytes.Length))
            .IndexOf("%PDF-", StringComparison.Ordinal) < 0)
            throw new InvalidDataException("The selected file is not a PDF.");
        return bytes;
    }
}
