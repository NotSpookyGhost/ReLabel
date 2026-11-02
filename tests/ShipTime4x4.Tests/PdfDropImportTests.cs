using ShipTime4x4.Hotfolder.Services;

namespace ShipTime4x4.Tests;

public sealed class PdfDropImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReLabelDropTests", Guid.NewGuid().ToString("N"));
    private string Incoming => Path.Combine(_root, "Incoming");

    [Theory]
    [InlineData("label.pdf", true)]
    [InlineData("LABEL.PDF", true)]
    [InlineData("label.pdf.exe", false)]
    [InlineData("image.png", false)]
    [InlineData("", false)]
    public void OnlyPdfExtensionsAreAccepted(string path, bool expected) =>
        Assert.Equal(expected, PdfDropImport.IsPdfPath(path));

    [Fact]
    public async Task CopyPreservesOriginalAndRenamesDuplicate()
    {
        var source = Source("label.pdf", "first");
        var first = await PdfDropImport.CopyToIncomingAsync(source, Incoming);
        File.WriteAllText(source, "second");
        var second = await PdfDropImport.CopyToIncomingAsync(source, Incoming);
        Assert.Equal("label.pdf", Path.GetFileName(first));
        Assert.Equal("label (1).pdf", Path.GetFileName(second));
        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
        Assert.Equal("second", File.ReadAllText(source));
        Assert.Empty(Directory.GetFiles(Incoming, "*.tmp"));
    }

    [Fact]
    public async Task FilesAlreadyInIncomingAreNotDuplicated()
    {
        Directory.CreateDirectory(Incoming);
        var source = Path.Combine(Incoming, "label.pdf");
        File.WriteAllText(source, "existing");
        Assert.Equal(source, await PdfDropImport.CopyToIncomingAsync(source, Incoming));
        Assert.Single(Directory.GetFiles(Incoming));
    }

    [Fact]
    public async Task ConcurrentCopiesDoNotOverwriteEachOther()
    {
        var source = Source("label.PDF", "complete");
        var copies = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => PdfDropImport.CopyToIncomingAsync(source, Incoming)));
        Assert.Equal(6, copies.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(copies, file => Assert.Equal("complete", File.ReadAllText(file)));
        Assert.Empty(Directory.GetFiles(Incoming, "*.tmp"));
    }

    [Fact]
    public async Task MissingFilesAndPdfNamedDirectoriesAreRejected()
    {
        var directory = Path.Combine(_root, "folder.pdf");
        Directory.CreateDirectory(directory);
        await Assert.ThrowsAsync<FileNotFoundException>(() => PdfDropImport.CopyToIncomingAsync(directory, Incoming));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            PdfDropImport.CopyToIncomingAsync(Path.Combine(_root, "missing.pdf"), Incoming));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PdfDropImport.CopyToIncomingAsync(Source("notes.txt", "not a PDF"), Incoming));
    }

    [Fact]
    public async Task LockedSourceDoesNotCreatePartialPdf()
    {
        var source = Source("locked.pdf", "contents");
        using var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => PdfDropImport.CopyToIncomingAsync(source, Incoming));
        Assert.Empty(Directory.GetFiles(Incoming));
    }

    private string Source(string name, string contents)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
