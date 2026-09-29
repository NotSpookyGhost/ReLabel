using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.Services;
using ShipTime4x4.Core.Processing;

namespace ShipTime4x4.Tests;

public sealed class HotfolderTests
{
    [Fact]
    public void ShippingAddressRegionLocatorFindsFromAndToButExcludesDetailRows()
    {
        var tsv = string.Join('\n', new[]
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            TsvWord(1, 1, 1, 20, 40, 65, 18, "FROM/DE"),
            TsvWord(1, 1, 2, 220, 40, 38, 18, "TO/A"),
            TsvWord(1, 2, 1, 20, 70, 120, 16, "WAREHOUSE"),
            TsvWord(1, 2, 2, 220, 70, 105, 16, "CRAIG WALKER"),
            TsvWord(1, 3, 1, 20, 94, 130, 16, "45 HANOVER DR"),
            TsvWord(1, 3, 2, 220, 94, 130, 16, "303 DARLING ST"),
            TsvWord(1, 4, 1, 20, 118, 115, 16, "L2W 1A3"),
            TsvWord(1, 4, 2, 220, 118, 115, 16, "N3S 3X9"),
            TsvWord(1, 5, 1, 20, 142, 125, 16, "905-988-5141"),
            TsvWord(1, 6, 1, 20, 250, 45, 16, "DATE:"),
            TsvWord(1, 6, 2, 220, 250, 70, 16, "WEIGHT")
        });

        var regions = ShippingAddressRegionLocator.Locate(tsv, 500, 700, 300);

        Assert.Equal(2, regions.Count);
        Assert.All(regions, region => Assert.True(region.Bottom < 200));
        Assert.True(regions[0].Right < regions[1].X);
        Assert.Contains(regions, region => region.Contains(30, 75));
        Assert.Contains(regions, region => region.Contains(230, 75));
        Assert.Contains(regions, region => region.Contains(30, 150));
    }

    private static string TsvWord(int block, int line, int word, int left, int top, int width, int height,
        string text) => $"5\t1\t{block}\t1\t{line}\t{word}\t{left}\t{top}\t{width}\t{height}\t95\t{text}";

    [Theory]
    [InlineData("SHIP TO: Jane Smith\n123 Main Street", "Jane Smith")]
    [InlineData("CONSIGNEE\nAcme Receiving\n500 Warehouse Rd", "Acme Receiving")]
    [InlineData("No recipient marker here", "Unknown recipient")]
    public void RecipientParserUsesTextMarkers(string text, string expected)
    {
        Assert.Equal(expected, RecipientNameParser.Parse(text));
    }

    [Fact]
    public void RecipientParserUsesToColumnCoordinates()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
            "5\t1\t1\t1\t1\t1\t100\t100\t80\t30\t95\tFROM\n" +
            "5\t1\t2\t1\t1\t1\t1200\t100\t75\t30\t95\tTO/A\n" +
            "5\t1\t3\t1\t1\t1\t100\t180\t120\t35\t95\tSender\n" +
            "5\t1\t4\t1\t1\t1\t1210\t180\t120\t35\t95\tCRAIG\n" +
            "5\t1\t4\t1\t1\t2\t1345\t180\t140\t35\t95\tWALKER\n";

        Assert.Equal("CRAIG WALKER", RecipientNameParser.Parse("FROM / DE\nTO/A\nSender\nCRAIG WALKER", tsv));
    }

    [Fact]
    public void DestinationParserReturnsAddressAfterRecipient()
    {
        const string text = "FROM / DE\nTO / A\nCRAIG WALKER\nCRAIG WALKER\n303 DARLING ST\nBRANTFORD, ON\nN3S 3X9\n226-920-5508";

        var result = DestinationAddressParser.Parse(text, null, "CRAIG WALKER");

        Assert.Equal("303 DARLING ST, BRANTFORD, ON, N3S 3X9", result);
    }

    [Theory]
    [InlineData(203)]
    [InlineData(300)]
    [InlineData(600)]
    public void HotfolderConfigurationAcceptsSupportedDpi(int dpi)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var configuration = new HotfolderConfiguration
        {
            IncomingFolder = Path.Combine(root, "Incoming"),
            ArchiveFolder = Path.Combine(root, "Archive"),
            ReadyFolder = Path.Combine(root, "Ready"),
            PrintedFolder = Path.Combine(root, "Printed"),
            PrintResolutionDpi = dpi
        };

        configuration.Validate();
    }

    [Theory]
    [InlineData(203, 10)]
    [InlineData(300, 8)]
    [InlineData(600, 4)]
    public void HotfolderConfigurationAcceptsMaximumZebraSpeed(int dpi, double speed)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        new HotfolderConfiguration
        {
            IncomingFolder = Path.Combine(root, "Incoming"),
            ArchiveFolder = Path.Combine(root, "Archive"),
            ReadyFolder = Path.Combine(root, "Ready"),
            PrintedFolder = Path.Combine(root, "Printed"),
            PrintResolutionDpi = dpi,
            PrintQuality = PrintQualityPreset.HighQuality,
            PrintDarkness = 12.5,
            PrintSpeedIps = speed
        }.Validate();
    }

    [Fact]
    public void HotfolderConfigurationRejectsUnsupportedSpeed()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var configuration = new HotfolderConfiguration
        {
            IncomingFolder = Path.Combine(root, "Incoming"),
            ArchiveFolder = Path.Combine(root, "Archive"),
            ReadyFolder = Path.Combine(root, "Ready"),
            PrintedFolder = Path.Combine(root, "Printed"),
            PrintResolutionDpi = 600,
            PrintSpeedIps = 10
        };

        Assert.Throws<InvalidOperationException>(configuration.Validate);
    }

    [Fact]
    public void InstallerInitializerCreatesSelectedFoldersAndInitialSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReLabelTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        var incoming = Path.Combine(root, "Drop");
        var ready = Path.Combine(root, "Waiting");
        var archive = Path.Combine(root, "Originals");
        var printed = Path.Combine(root, "Complete");

        var created = InstallerConfigurationInitializer.InitializeIfMissing(
            data, incoming, ready, archive, printed);
        var configuration = new ConfigurationStore(data).Load();

        Assert.True(created);
        Assert.Equal(incoming, configuration.IncomingFolder);
        Assert.Equal(ready, configuration.ReadyFolder);
        Assert.Equal(archive, configuration.ArchiveFolder);
        Assert.Equal(printed, configuration.PrintedFolder);
        Assert.All([incoming, ready, archive, printed], folder => Assert.True(Directory.Exists(folder)));
        Directory.Delete(root, true);
    }

    [Fact]
    public void InstallerInitializerNeverChangesExistingSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReLabelTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        var existingRoot = Path.Combine(root, "Existing");
        var store = new ConfigurationStore(data);
        var existing = new HotfolderConfiguration
        {
            IncomingFolder = Path.Combine(existingRoot, "Incoming"),
            ReadyFolder = Path.Combine(existingRoot, "Ready"),
            ArchiveFolder = Path.Combine(existingRoot, "Archive"),
            PrintedFolder = Path.Combine(existingRoot, "Printed")
        };
        store.Save(existing);

        var created = InstallerConfigurationInitializer.InitializeIfMissing(data,
            Path.Combine(root, "NewIncoming"), Path.Combine(root, "NewReady"),
            Path.Combine(root, "NewArchive"), Path.Combine(root, "NewPrinted"));

        Assert.False(created);
        Assert.Equal(existing, store.Load());
        Directory.Delete(root, true);
    }

    [Fact]
    public void PrintedFolderCannotBeNestedInsideIncomingFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var configuration = new HotfolderConfiguration
        {
            IncomingFolder = root,
            ArchiveFolder = Path.Combine(Path.GetDirectoryName(root)!, "Archive-" + Guid.NewGuid().ToString("N")),
            ReadyFolder = Path.Combine(Path.GetDirectoryName(root)!, "Ready-" + Guid.NewGuid().ToString("N")),
            PrintedFolder = Path.Combine(root, "Printed")
        };

        Assert.Throws<InvalidOperationException>(configuration.Validate);
    }

    [Fact]
    public void SuccessfulPrintSourceMovesIntoOrganizedPrintedFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReLabelTests", Guid.NewGuid().ToString("N"));
        var incoming = Path.Combine(root, "Ready");
        var printed = Path.Combine(root, "Printed");
        Directory.CreateDirectory(incoming);
        var source = Path.Combine(incoming, "label.pdf");
        File.WriteAllText(source, "test");
        var configuration = new HotfolderConfiguration
        {
            IncomingFolder = Path.Combine(root, "Incoming"),
            ReadyFolder = incoming,
            ArchiveFolder = Path.Combine(root, "Archive"),
            PrintedFolder = printed
        };
        var record = new LabelRecord
        {
            ShipToName = "Jane Smith",
            OriginalFileName = "label.pdf",
            SourcePath = source
        };

        var destination = PrintedFileMover.MoveToPrinted(record, configuration);

        Assert.NotNull(destination);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
        Assert.Contains(DateTime.Now.ToString("yyyy"), destination!);
        Assert.Contains("Jane Smith", destination!);
        Directory.Delete(root, true);
    }

    [Fact]
    public void PrintedLabelCanBeDeletedFromCatalogAndPrintedFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReLabelTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        var printed = Path.Combine(root, "Printed");
        Directory.CreateDirectory(printed);
        var pdf = Path.Combine(printed, "label.pdf");
        File.WriteAllText(pdf, "test");
        var catalog = new LabelCatalog(data);
        var record = new LabelRecord { OriginalFileName = "label.pdf", PrintedPath = pdf, PrintedAt = DateTimeOffset.Now };
        catalog.Add(record);
        using var coordinator = new HotfolderCoordinator(data, AppContext.BaseDirectory);
        coordinator.ApplyConfiguration(new HotfolderConfiguration
        {
            IncomingFolder = Path.Combine(root, "Incoming"),
            ArchiveFolder = Path.Combine(root, "Archive"),
            ReadyFolder = Path.Combine(root, "Ready"),
            PrintedFolder = printed,
            WatchEnabled = false
        }, save: false);

        coordinator.DeletePrinted(record.Id);

        Assert.False(File.Exists(pdf));
        Assert.DoesNotContain(coordinator.Labels, item => item.Id == record.Id);
        Directory.Delete(root, true);
    }

    [Fact]
    public void PrintedLabelsExpireAfterFortyEightHours()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReLabelTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        var printed = Path.Combine(root, "Printed");
        Directory.CreateDirectory(printed);
        var expiredPdf = Path.Combine(printed, "expired.pdf");
        var recentPdf = Path.Combine(printed, "recent.pdf");
        File.WriteAllText(expiredPdf, "old");
        File.WriteAllText(recentPdf, "new");
        var now = DateTimeOffset.Now;
        var catalog = new LabelCatalog(data);
        catalog.Add(new LabelRecord { OriginalFileName = "expired.pdf", PrintedPath = expiredPdf, PrintedAt = now.AddHours(-49) });
        catalog.Add(new LabelRecord { OriginalFileName = "recent.pdf", PrintedPath = recentPdf, PrintedAt = now.AddHours(-47) });
        using var coordinator = new HotfolderCoordinator(data, AppContext.BaseDirectory);
        coordinator.ApplyConfiguration(new HotfolderConfiguration
        {
            IncomingFolder = Path.Combine(root, "Incoming"), ArchiveFolder = Path.Combine(root, "Archive"),
            ReadyFolder = Path.Combine(root, "Ready"),
            PrintedFolder = printed, WatchEnabled = false
        }, save: false);

        var removed = coordinator.PurgeExpiredPrinted(now);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(expiredPdf));
        Assert.True(File.Exists(recentPdf));
        Assert.Single(coordinator.Labels);
        Directory.Delete(root, true);
    }

    [Fact]
    public void ArchiveCannotBeNestedInsideIncomingFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var configuration = new HotfolderConfiguration
        {
            IncomingFolder = root,
            ArchiveFolder = Path.Combine(root, "Archive"),
            ReadyFolder = Path.Combine(Path.GetDirectoryName(root)!, "Ready-" + Guid.NewGuid().ToString("N"))
        };

        Assert.Throws<InvalidOperationException>(configuration.Validate);
    }

    [Fact]
    public void ExistingSettingsAreMigratedToRequiredSafeMargin()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReLabelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), """
        {
          "IncomingFolder": "C:\\ReLabel\\Incoming",
          "ArchiveFolder": "C:\\ReLabel\\Archive",
          "ReadyFolder": "C:\\ReLabel\\Ready",
          "PrintedFolder": "C:\\ReLabel\\Printed",
          "MarginInches": 0.04,
          "PrintResolutionDpi": 203
        }
        """);

        var configuration = new ConfigurationStore(root).Load();

        Assert.Equal(0.125, configuration.MarginInches);
        Assert.Equal(LabelFitMode.Proportional, configuration.DefaultFitMode);
        Assert.Equal(4, configuration.DefaultLabelWidthInches);
        Assert.Equal(4, configuration.DefaultLabelHeightInches);
        Assert.Equal(LabelRotation.None, configuration.DefaultRotation);
        Directory.Delete(root, true);
    }

    [Fact]
    public void NewLabelDefaultsAreSavedAndLoaded()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReLabelTests", Guid.NewGuid().ToString("N"));
        var store = new ConfigurationStore(root);
        var configuration = new HotfolderConfiguration
        {
            IncomingFolder = Path.Combine(root, "Incoming"),
            ArchiveFolder = Path.Combine(root, "Archive"),
            ReadyFolder = Path.Combine(root, "Ready"),
            PrintedFolder = Path.Combine(root, "Printed"),
            DefaultFitMode = LabelFitMode.SquishToFill,
            DefaultLabelWidthInches = 4,
            DefaultLabelHeightInches = 6,
            DefaultRotation = LabelRotation.Clockwise90,
            DefaultBarcodeCompensation = RotatedBarcodeCompensation.OneDot,
            DefaultTextEnhancement = TextEnhancement.Custom,
            DefaultFromAddressScalePercent = 185,
            DefaultToAddressScalePercent = 225
        };

        store.Save(configuration);
        var loaded = store.Load();

        Assert.Equal(LabelFitMode.SquishToFill, loaded.DefaultFitMode);
        Assert.Equal(4, loaded.DefaultLabelWidthInches);
        Assert.Equal(6, loaded.DefaultLabelHeightInches);
        Assert.Equal(LabelRotation.Clockwise90, loaded.DefaultRotation);
        Assert.Equal(RotatedBarcodeCompensation.OneDot, loaded.DefaultBarcodeCompensation);
        Assert.Equal(TextEnhancement.Custom, loaded.DefaultTextEnhancement);
        Assert.Equal(185, loaded.EffectiveDefaultFromAddressScalePercent);
        Assert.Equal(225, loaded.EffectiveDefaultToAddressScalePercent);
        Directory.Delete(root, true);
    }

    [Fact]
    public void UnsupportedNewLabelDefaultSizeIsRejected()
    {
        var configuration = new HotfolderConfiguration
        {
            DefaultLabelWidthInches = 5,
            DefaultLabelHeightInches = 7
        };

        Assert.Throws<InvalidOperationException>(configuration.Validate);
    }

    [Fact]
    public void DuplicateReadyNamesUseIncrementingParentheses()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReLabelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "label.pdf"), "one");
        File.WriteAllText(Path.Combine(root, "label (1).pdf"), "two");

        var next = ManagedFileNames.NextAvailable(root, "label.pdf");

        Assert.Equal(Path.Combine(root, "label (2).pdf"), next);
        Directory.Delete(root, true);
    }

    [Fact]
    public void MissingReadyFileRemovesCatalogArchiveAndWritesAuditEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReLabelTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        var ready = Path.Combine(root, "Ready");
        var archive = Path.Combine(root, "Archive");
        Directory.CreateDirectory(ready);
        Directory.CreateDirectory(archive);
        var source = Path.Combine(ready, "label.pdf");
        var archiveFile = Path.Combine(archive, "label.pdf");
        File.WriteAllText(source, "source");
        File.WriteAllText(archiveFile, "archive");
        var record = new LabelRecord { OriginalFileName = "label.pdf", SourcePath = source, ArchivePath = archiveFile };
        new LabelCatalog(data).Add(record);
        using var coordinator = new HotfolderCoordinator(data, AppContext.BaseDirectory);
        coordinator.ApplyConfiguration(new HotfolderConfiguration
        {
            IncomingFolder = Path.Combine(root, "Incoming"), ReadyFolder = ready, ArchiveFolder = archive,
            PrintedFolder = Path.Combine(root, "Printed"), WatchEnabled = false
        }, save: false);
        File.Delete(source);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (coordinator.Labels.Count > 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        coordinator.ReconcileManagedFiles();

        Assert.Empty(coordinator.Labels);
        Assert.False(File.Exists(archiveFile));
        Assert.Contains("\"Action\":\"Delete\"", File.ReadAllText(coordinator.AuditLogPath));
        Directory.Delete(root, true);
    }

    [Fact]
    public void PerLabelOutputSettingsPersistForLaterPreviewAndReprint()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReLabelTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        var record = new LabelRecord { OriginalFileName = "label.pdf" };
        new LabelCatalog(data).Add(record);

        using (var coordinator = new HotfolderCoordinator(data, AppContext.BaseDirectory))
            coordinator.UpdateLabelSettings(record.Id, LabelFitMode.SquishToFill, 4, 8,
                LabelRotation.Clockwise270, RotatedBarcodeCompensation.OneDot, TextEnhancement.Custom, 185, 225);

        var saved = new LabelCatalog(data).Find(record.Id);
        Assert.NotNull(saved);
        Assert.Equal(LabelFitMode.SquishToFill, saved.FitMode);
        Assert.Equal(4, saved.OutputWidthInches);
        Assert.Equal(8, saved.OutputHeightInches);
        Assert.Equal(LabelRotation.Clockwise270, saved.Rotation);
        Assert.Equal(RotatedBarcodeCompensation.OneDot, saved.BarcodeCompensation);
        Assert.Equal(TextEnhancement.Custom, saved.TextEnhancement);
        Assert.Equal(185, saved.EffectiveFromAddressScalePercent);
        Assert.Equal(225, saved.EffectiveToAddressScalePercent);
        Assert.Contains("\"Action\":\"LabelSettings\"",
            File.ReadAllText(Path.Combine(data, "Logs", $"audit-{DateTime.UtcNow:yyyy-MM}.jsonl")));
        Directory.Delete(root, true);
    }
}
