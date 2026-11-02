using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ShipTime4x4.Core.Raster;
using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.Services;

namespace ShipTime4x4.Tests;

internal sealed class SecurityTestClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}

public sealed class SecurityTests : IDisposable
{
    private static string ApplicationFolder =>
        Environment.GetEnvironmentVariable("RELABEL_TEST_APPLICATION") ?? AppContext.BaseDirectory;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReLabelSecurityTests", Guid.NewGuid().ToString("N"));

    private CarrierTemplate Template() => new()
    {
        Carrier = "Synthetic carrier", LayoutName = "Security fixture",
        Zones =
        [
            new TemplateZone { Type = TemplateZoneType.ToAddress },
            new TemplateZone { Type = TemplateZoneType.ShippingBarcode }
        ]
    };

    private string Package(CarrierTemplate template)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid() + ".relabel-template");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("template.json").Open()))
            writer.Write(JsonSerializer.Serialize(new { FormatVersion = 1, Template = template }));
        using var source = new StreamWriter(zip.CreateEntry("sample.pdf").Open());
        source.Write("%PDF-1.4\nSynthetic store-validation fixture\n%%EOF");
        return path;
    }

    [Fact]
    public void ArchiveExtractionEnforcesActualExpandedSize()
    {
        using var input = new MemoryStream(new byte[100]);
        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => CarrierTemplateStore.CopyBounded(input, output, 50));
        Assert.True(output.Length <= 50);
    }

    [Fact]
    public void NullMatchingFeaturesCannotPersistThroughPackageImport()
    {
        var store = new CarrierTemplateStore(_root);
        var package = Package(Template() with { Features = null! });
        Assert.Throws<InvalidDataException>(() => store.ImportPackage(package));
        Assert.Empty(new CarrierTemplateStore(_root).GetAll());
    }

    [Fact]
    public void InvalidTemplateCannotBreakMatchingForOtherTemplates()
    {
        var bad = Template() with { Features = null! };
        var inspection = new PdfInspection(GrayImage.White(40, 60), [], "", "", 0);
        var result = new CarrierTemplateEngine().Match(inspection, [bad]);
        Assert.Null(result.Template);
    }

    [Fact]
    public void SampleInspectionFailureDoesNotCommitTemplate()
    {
        var store = new CarrierTemplateStore(_root);
        var package = Package(Template());
        Assert.Throws<InvalidDataException>(() => store.ImportPackage(package,
            _ => throw new InvalidDataException("Synthetic rejected sample")));
        Assert.Empty(new CarrierTemplateStore(_root).GetAll());
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "CarrierTemplates")));
    }

    [Fact]
    public void InvalidSavedMetadataIsBackedUpAndSkippedOnRestart()
    {
        var store = new CarrierTemplateStore(_root);
        var saved = store.ImportPackage(Package(Template()));
        var catalogPath = Path.Combine(_root, "CarrierTemplates", "templates.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            Version = 2, Templates = new[] { saved with { Features = null! } }
        }));
        Assert.Empty(new CarrierTemplateStore(_root).GetAll());
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(catalogPath)!, "templates.json.invalid-*"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void NonFiniteOrOutOfRangeZonesAreRejected(double coordinate)
    {
        var template = Template();
        var zones = template.Zones.ToArray();
        zones[0] = zones[0] with { Source = new NormalizedRect(coordinate, 0, .2, .2) };
        Assert.Throws<InvalidDataException>(() => CarrierTemplateValidator.ValidateMetadata(template with { Zones = zones }));
    }

    [Fact]
    public void FileSizeLimitIsCheckedBeforeReadingPdfIntoMemory()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "oversized.pdf");
        using (var file = File.Create(path)) file.SetLength(2 * 1024 * 1024);
        Assert.Throws<InvalidDataException>(() => new PdfSafetyLimits { MaximumFileMegabytes = 1 }.ReadPdf(path));
    }

    [Fact]
    public void RenderBudgetAcceptsLetterAt600DpiButRejectsHugeOrNonFinitePages()
    {
        var limits = new PdfSafetyLimits();
        limits.ValidatePageSize(612, 792, 600);
        Assert.Throws<InvalidDataException>(() => limits.ValidatePageSize(14400, 14400, 300));
        Assert.Throws<InvalidDataException>(() => limits.ValidatePageSize(double.PositiveInfinity, 432, 300));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void WorkerResponseTextLengthsAreBounded(int length)
    {
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.UTF8, true)) writer.Write(length);
        data.Position = 0;
        using var reader = new BinaryReader(data);
        Assert.Throws<InvalidDataException>(() => PdfWorkerClient.ReadText(reader, 4096));
    }

    [Fact]
    public void RestrictedWorkerRejectsExcessPagesWithoutRendering()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "two-pages.pdf");
        MultiPagePdfTests.WritePdf(path, 2);
        var processor = new PdfLabelProcessor(ApplicationFolder)
            { Limits = new PdfSafetyLimits { MaximumPages = 1 } };
        var error = Assert.Throws<InvalidDataException>(() => processor.Inspect(path));
        Assert.Contains("maximum 1 pages", error.Message);
    }

    [Fact]
    public void RestrictedWorkerRejectsHugePageBeforeRasterAllocation()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "huge-page.pdf");
        MultiPagePdfTests.WritePdf(path, 1, 14400, 14400);
        var error = Assert.Throws<InvalidDataException>(() => new PdfLabelProcessor(ApplicationFolder).Inspect(path));
        Assert.Contains("megapixel", error.Message);
    }

    [Fact]
    public void MissingConfirmationResetsAfterAccessFailureOrFileReappears()
    {
        var clock = new SecurityTestClock();
        var state = ManagedFileState.Missing;
        var monitor = new ManagedFileMonitor(clock, (_, _) => state);
        Assert.False(monitor.ConfirmMissing("label", "root"));
        clock.Advance(TimeSpan.FromSeconds(6));
        state = ManagedFileState.Unavailable;
        Assert.False(monitor.ConfirmMissing("label", "root"));
        state = ManagedFileState.Missing;
        Assert.False(monitor.ConfirmMissing("label", "root"));
        clock.Advance(TimeSpan.FromSeconds(6));
        state = ManagedFileState.Present;
        Assert.False(monitor.ConfirmMissing("label", "root"));
        state = ManagedFileState.Missing;
        Assert.False(monitor.ConfirmMissing("label", "root"));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(monitor.ConfirmMissing("label", "root"));
    }

    [Fact]
    public void UnavailableReadyRootPreservesCatalogAndArchive()
    {
        Directory.CreateDirectory(_root);
        var ready = Path.Combine(_root, "Ready");
        var archive = Path.Combine(_root, "Archive");
        Directory.CreateDirectory(ready);
        Directory.CreateDirectory(archive);
        var path = Path.Combine(ready, "label.pdf");
        var original = Path.Combine(archive, "label.pdf");
        File.WriteAllText(path, "fixture");
        File.WriteAllText(original, "fixture");
        var data = Path.Combine(_root, "Data");
        new LabelCatalog(data).Add(new LabelRecord { SourcePath = path, ArchivePath = original });
        var clock = new SecurityTestClock();
        using var coordinator = new HotfolderCoordinator(data, AppContext.BaseDirectory, clock: clock);
        coordinator.ApplyConfiguration(new HotfolderConfiguration
        {
            IncomingFolder = Path.Combine(_root, "Incoming"), ReadyFolder = ready,
            ArchiveFolder = archive, PrintedFolder = Path.Combine(_root, "Printed"), WatchEnabled = false
        }, save: false);
        Directory.Move(ready, ready + "-offline");
        coordinator.ReconcileManagedFiles();
        clock.Advance(TimeSpan.FromMinutes(1));
        coordinator.ReconcileManagedFiles();
        Assert.Single(coordinator.Labels);
        Assert.True(File.Exists(original));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
