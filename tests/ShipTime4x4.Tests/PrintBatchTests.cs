using System.Text;
using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.Printing;
using ShipTime4x4.Hotfolder.Services;

namespace ShipTime4x4.Tests;

public sealed class PrintBatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotIncludesOnlyUnprintedReadyAndWarningFilesInDisplayedOrder(bool oldestFirst)
    {
        var older = new LabelRecord { ImportedAt = DateTimeOffset.UtcNow.AddHours(-1), PageCount = 2 };
        var newer = new LabelRecord { ImportedAt = DateTimeOffset.UtcNow, Status = LabelStatus.Warning };
        var snapshot = PrintBatch.Snapshot([
            newer, older, new() { Status = LabelStatus.Failed },
            new() { Status = LabelStatus.Printing }, new() { Status = LabelStatus.Printed },
            new() { PrintedAt = DateTimeOffset.Now, Status = LabelStatus.Warning }
        ], oldestFirst);
        Assert.Equal(oldestFirst ? new[] { older.Id, newer.Id } : new[] { newer.Id, older.Id },
            snapshot.Select(record => record.Id));
        Assert.Equal(3, snapshot.Sum(record => record.PageCount));
    }

    [Fact]
    public void SnapshotExcludesLaterArrivalsAndDuplicateIds()
    {
        var first = new LabelRecord();
        var source = new List<LabelRecord> { first, first };
        var snapshot = PrintBatch.Snapshot(source, false);
        source.Add(new LabelRecord());
        source.Clear();
        Assert.Single(snapshot);
        Assert.Equal(first.Id, snapshot[0].Id);
    }

    [Fact]
    public async Task WaitsForEachPrintAndVerificationBeforeStartingTheNext()
    {
        LabelRecord[] files = [new(), new()];
        var first = new TaskCompletionSource<BatchPrintOutcome>();
        var visited = new List<Guid>();
        var progress = new List<int>();
        var task = PrintBatch.RunAsync(files, record =>
        {
            visited.Add(record.Id);
            return visited.Count == 1 ? first.Task : Task.FromResult(BatchPrintOutcome.Sent);
        }, () => false, (index, total) => { Assert.Equal(2, total); progress.Add(index); });
        Assert.Equal(new[] { files[0].Id }, visited);
        first.SetResult(BatchPrintOutcome.Sent);
        var result = await task;
        Assert.Equal(files.Select(record => record.Id), visited);
        Assert.Equal(new[] { 1, 2 }, progress);
        Assert.Equal(new BatchPrintResult(2, 2, 0, false), result);
    }

    [Fact]
    public async Task StopAfterCurrentFileDoesNotSubmitRemainingFiles()
    {
        var stop = false;
        var calls = 0;
        var result = await PrintBatch.RunAsync([new(), new(), new()], _ =>
        {
            calls++;
            stop = true;
            return Task.FromResult(BatchPrintOutcome.Sent);
        }, () => stop, (_, _) => { });
        Assert.Equal(1, calls);
        Assert.Equal(new BatchPrintResult(3, 1, 0, true), result);
        Assert.Equal(2, result.Remaining);
    }

    [Fact]
    public async Task StopBeforeStartingSubmitsNothing()
    {
        var result = await PrintBatch.RunAsync([new()], _ =>
            throw new InvalidOperationException("Should not submit"), () => true, (_, _) => { });
        Assert.Equal(new BatchPrintResult(1, 0, 0, true), result);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    public async Task CancelledConfirmationOrScanStopsBatch(int outcome, int sent)
    {
        var calls = 0;
        var result = await PrintBatch.RunAsync([new(), new()], _ =>
        {
            calls++;
            return Task.FromResult((BatchPrintOutcome)outcome);
        }, () => false, (_, _) => { });
        Assert.Equal(1, calls);
        Assert.Equal(new BatchPrintResult(2, sent, 0, true), result);
    }

    [Fact]
    public async Task AlreadyHandledFilesAreCountedAsSkipped()
    {
        var calls = 0;
        var result = await PrintBatch.RunAsync([new(), new()], _ =>
            Task.FromResult(++calls == 1 ? BatchPrintOutcome.Skipped : BatchPrintOutcome.Sent),
            () => false, (_, _) => { });
        Assert.Equal(new BatchPrintResult(2, 1, 1, false), result);
        Assert.Equal(0, result.Remaining);
    }

    [Fact]
    public async Task UnexpectedFailureNeverContinuesToNextFile()
    {
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => PrintBatch.RunAsync([new(), new()], _ =>
        {
            calls++;
            throw new IOException("Printer unavailable");
        }, () => false, (_, _) => { }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReadyOnlyChecksPreventDuplicateSubmissionsButExplicitReprintStillWorks()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReLabelBatchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new HotfolderConfiguration
            {
                IncomingFolder = Path.Combine(root, "Incoming"), ReadyFolder = Path.Combine(root, "Ready"),
                ArchiveFolder = Path.Combine(root, "Archive"), PrintedFolder = Path.Combine(root, "Printed"),
                PhysicalPrinterQueue = "Fake test printer", WatchEnabled = false
            };
            var data = Path.Combine(root, "Data");
            var printer = new RecordingPrinter();
            foreach (var folder in new[] { configuration.IncomingFolder, configuration.ReadyFolder,
                         configuration.ArchiveFolder, configuration.PrintedFolder })
                Directory.CreateDirectory(folder);
            var source = Path.Combine(configuration.ReadyFolder, "two-pages.pdf");
            var archive = Path.Combine(configuration.ArchiveFolder, "two-pages.pdf");
            MultiPagePdfTests.WritePdf(source, 2);
            File.Copy(source, archive);
            var record = new LabelRecord
            {
                SourcePath = source, ArchivePath = archive, OriginalFileName = "two-pages.pdf", PageCount = 2
            };
            new LabelCatalog(data).Add(record);
            using var printing = new HotfolderCoordinator(data,
                Environment.GetEnvironmentVariable("RELABEL_TEST_APPLICATION") ?? AppContext.BaseDirectory, printer);
            printing.ApplyConfiguration(configuration, save: false);
            var results = await Task.WhenAll(
                printing.PrintAsync(record.Id, requireReady: true),
                printing.PrintAsync(record.Id, requireReady: true));
            Assert.Equal(1, results.Count(sent => sent));
            Assert.Single(printer.Jobs);
            Assert.Equal(2, printer.Jobs[0].Split("^XA").Length - 1);
            Assert.True(await printing.PrintAsync(record.Id));
            Assert.Equal(2, printer.Jobs.Count);
            Assert.False(await printing.PrintAsync(Guid.NewGuid(), requireReady: true));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RecordingPrinter : IRawPrinterClient
    {
        public List<string> Jobs { get; } = [];
        public void Print(string printerName, string documentName, ReadOnlySpan<byte> data)
            => Jobs.Add(Encoding.ASCII.GetString(data));
    }
}
