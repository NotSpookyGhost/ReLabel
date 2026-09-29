using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using ShipTime4x4.Core.Processing;
using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.Printing;

namespace ShipTime4x4.Hotfolder.Services;

public sealed class HotfolderCoordinator : IDisposable
{
    private readonly ConfigurationStore _configurationStore;
    private readonly LabelCatalog _catalog;
    private readonly PdfLabelProcessor _processor;
    private readonly IRawPrinterClient _printer;
    private readonly IPrinterStatusProvider? _printerStatusProvider;
    private readonly AuditLog _audit;
    private readonly PreparedLabelDiskCache _diskCache;
    private readonly CarrierTemplateStore _templateStore;
    private readonly CarrierTemplateEngine _templateEngine = new();
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _printGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedPreparedLabel>>> _preparedCache = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private readonly System.Threading.Timer _retentionTimer;
    private readonly System.Threading.Timer _printerStatusTimer;
    private FileSystemWatcher? _incomingWatcher;
    private FileSystemWatcher? _readyWatcher;
    private FileSystemWatcher? _printedWatcher;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _internalFileChanges = new(StringComparer.OrdinalIgnoreCase);
    private HotfolderConfiguration _configuration;
    private int _printerStatusCheckActive;

    public HotfolderCoordinator(string dataFolder, string applicationFolder, IRawPrinterClient? printer = null)
    {
        _configurationStore = new ConfigurationStore(dataFolder);
        _catalog = new LabelCatalog(dataFolder);
        _processor = new PdfLabelProcessor(applicationFolder);
        _printer = printer ?? new RawPrinterClient();
        _printerStatusProvider = _printer as IPrinterStatusProvider;
        _audit = new AuditLog(dataFolder);
        _diskCache = new PreparedLabelDiskCache(dataFolder);
        _templateStore = new CarrierTemplateStore(dataFolder);
        _configuration = _configurationStore.Load();
        _worker = Task.Run(ProcessIncomingAsync);
        _retentionTimer = new System.Threading.Timer(_ => PurgeExpiredPrinted(DateTimeOffset.Now), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _printerStatusTimer = new System.Threading.Timer(_ => RefreshPrinterStatus(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler? CatalogChanged;
    public event EventHandler<string>? ActivityChanged;
    public event EventHandler<PrintProgressEventArgs>? PrintProgressChanged;
    public event EventHandler<PrinterStatusSnapshot>? PrinterStatusChanged;

    public HotfolderConfiguration Configuration => _configuration;
    public IReadOnlyList<LabelRecord> Labels => _catalog.GetAll();
    public IReadOnlyList<CarrierTemplate> Templates => _templateStore.GetAll();
    public string AuditLogPath => _audit.CurrentPath;

    public void Start()
    {
        ApplyConfiguration(_configuration, save: false);
        MigrateLegacyReadyFiles();
        ReconcileManagedFiles();
        RecoverLegacyMultiPageLabels();
        PurgeExpiredPrinted(DateTimeOffset.Now);
        _retentionTimer.Change(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        _printerStatusTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(4));
        _ = Task.Run(() => BackfillMissingAddresses(_stopping.Token));
        _ = Task.Run(() => PreloadReadyLabelsAsync(_stopping.Token));
    }

    public void ApplyConfiguration(HotfolderConfiguration configuration, bool save = true)
    {
        configuration.Validate();
        Directory.CreateDirectory(configuration.IncomingFolder);
        Directory.CreateDirectory(configuration.ReadyFolder);
        Directory.CreateDirectory(configuration.ArchiveFolder);
        Directory.CreateDirectory(configuration.PrintedFolder);
        DisposeWatchers();
        _configuration = configuration;
        _preparedCache.Clear();
        if (save)
            _configurationStore.Save(configuration);

        if (configuration.WatchEnabled)
        {
            _incomingWatcher = CreateWatcher(configuration.IncomingFolder, false);
            _incomingWatcher.Created += OnFileAvailable;
            _incomingWatcher.Changed += OnFileAvailable;
            _incomingWatcher.Renamed += OnFileRenamed;
            _incomingWatcher.Error += (_, arguments) =>
            {
                Report($"Incoming folder watcher issue: {arguments.GetException().Message}. Checking the folder again...");
                QueueExistingFiles();
            };
            Report("Checking for new labels...");
            var queued = QueueExistingFiles();
            Report(queued == 0 ? "No new labels found" : $"Importing {queued} new label{(queued == 1 ? string.Empty : "s")}...");
        }
        else
        {
            Report("Folder watching is paused");
        }
        _readyWatcher = CreateWatcher(configuration.ReadyFolder, true);
        _readyWatcher.Deleted += OnManagedFileDeleted;
        _readyWatcher.Renamed += OnManagedFileRenamed;
        _readyWatcher.Error += (_, arguments) =>
        {
            Report($"Ready folder watcher issue: {arguments.GetException().Message}. Checking managed labels...");
            ReconcileManagedFiles();
        };
        _printedWatcher = CreateWatcher(configuration.PrintedFolder, true);
        _printedWatcher.Deleted += OnManagedFileDeleted;
        _printedWatcher.Renamed += OnManagedFileRenamed;
        _printedWatcher.Error += (_, arguments) =>
        {
            Report($"Printed folder watcher issue: {arguments.GetException().Message}. Checking managed labels...");
            ReconcileManagedFiles();
        };
    }

    public void QueueFile(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            return;
        var fullPath = Path.GetFullPath(path);
        if (_queued.TryAdd(fullPath, 0))
            _incoming.Writer.TryWrite(fullPath);
    }

    private static FileSystemWatcher CreateWatcher(string path, bool recursive) => new(path, "*.pdf")
    {
        IncludeSubdirectories = recursive,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
        EnableRaisingEvents = true
    };

    public async Task<PreparedLabel> PreparePreviewAsync(LabelRecord record, bool applyRotation = true,
        CancellationToken cancellationToken = default)
    {
        var result = await GetPreparedAsync(record, applyRotation, cancellationToken).ConfigureAwait(false);
        _audit.Write("Performance", record.Id, record.OriginalFileName, "Preview",
            $"Cache={result.Source}; PrepareMs={result.DurationMs}; RotationApplied={applyRotation}");
        return result.Label;
    }

    public void WarmPrintCache(LabelRecord record)
    {
        _ = GetPreparedAsync(record, applyRotation: true, CancellationToken.None)
            .ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public async Task PrintAsync(Guid id, CancellationToken cancellationToken = default,
        bool ignoreTemplate = false)
    {
        await _printGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = _catalog.Find(id) ?? throw new InvalidOperationException("The selected label no longer exists.");
            var totalTimer = Stopwatch.StartNew();
            try
            {
                ReportPrintProgress(record, record.PageCount > 1
                    ? $"Preparing {record.PageCount} labels..." : "Preparing label...");
                var preparedResult = await GetPreparedAsync(record, applyRotation: true, cancellationToken,
                    ignoreTemplate)
                    .ConfigureAwait(false);
                var prepared = preparedResult.Label;
                ReportPrintProgress(record, record.PageCount > 1
                    ? $"Sending {record.PageCount} labels to printer..." : "Sending to printer...");
                var spoolTimer = Stopwatch.StartNew();
                await Task.Run(() => _printer.Print(_configuration.PhysicalPrinterQueue,
                    $"ReLabel - {record.ShipToName}", prepared.Zpl), cancellationToken).ConfigureAwait(false);
                spoolTimer.Stop();
                ReportPrintProgress(record, record.PageCount > 1
                    ? $"Finalizing {record.PageCount} printed labels..." : "Finalizing printed label...");
                var finalizeTimer = Stopwatch.StartNew();
                string? printedPath = null;
                string? moveWarning = null;
                try
                {
                    MarkInternalFileChange(record.SourcePath);
                    printedPath = PrintedFileMover.MoveToPrinted(record, _configuration);
                    if (printedPath is null)
                        moveWarning = "The label printed, but its source PDF was no longer present in Incoming.";
                }
                catch (Exception exception)
                {
                    moveWarning = $"The label printed, but the source PDF could not be moved: {exception.Message}";
                }
                record = record with
                {
                    Status = prepared.UsedFallback || moveWarning is not null ? LabelStatus.Warning : LabelStatus.Printed,
                    StatusMessage = moveWarning ?? (prepared.UsedFallback
                        ? $"Printed with safe fallback ({prepared.WarningCode})"
                        : BarcodeCompensationStatus(record, prepared.AppliedBarcodeCompensationDots)),
                    PrintedAt = DateTimeOffset.Now,
                    PrintedPath = printedPath ?? record.PrintedPath,
                    UsedFallback = prepared.UsedFallback,
                    ScanVerification = _configuration.ScannerVerificationEnabled
                        ? ScanVerificationStatus.Pending : ScanVerificationStatus.NotRequired,
                    ScanVerifiedAt = null
                };
                Update(record);
                finalizeTimer.Stop();
                totalTimer.Stop();
                _audit.Write("Print", record.Id, record.OriginalFileName, "Success",
                    $"Pages={prepared.PageCount}; LabelDarkness={_configuration.PrintDarkness:0.0}; " +
                    $"BarcodeCompensationRequested={(int)record.BarcodeCompensation}dot; " +
                    $"BarcodeCompensationApplied={prepared.AppliedBarcodeCompensationDots}dot" +
                    (prepared.UsedFallback ? $"; Fallback={prepared.WarningCode}" : string.Empty));
                _audit.Write("Performance", record.Id, record.OriginalFileName, "Print",
                    $"Cache={preparedResult.Source}; PrepareMs={preparedResult.DurationMs}; " +
                    $"SpoolSubmitMs={spoolTimer.ElapsedMilliseconds}; FinalizeMs={finalizeTimer.ElapsedMilliseconds}; " +
                    $"TotalMs={totalTimer.ElapsedMilliseconds}; ZplBytes={prepared.Zpl.Length}");
                Report(record.PageCount > 1
                    ? $"Printed {record.PageCount} labels from {record.OriginalFileName}"
                    : $"Printed {record.OriginalFileName}");
            }
            catch (Exception exception)
            {
                record = record with { Status = LabelStatus.Failed, StatusMessage = exception.Message };
                Update(record);
                _audit.Write("Print", record.Id, record.OriginalFileName, "Failed", exception.Message);
                Report($"Print failed: {exception.Message}");
                throw;
            }
        }
        finally
        {
            _printGate.Release();
        }
    }

    private static string BarcodeCompensationStatus(LabelRecord record, int appliedDots)
    {
        var requestedDots = (int)record.BarcodeCompensation;
        if (requestedDots == 0 || record.Rotation is not (LabelRotation.Clockwise90 or LabelRotation.Clockwise270))
            return "Printed successfully";
        return appliedDots == requestedDots
            ? $"Printed successfully ({appliedDots}-dot barcode correction applied)"
            : $"Printed successfully (barcode correction safely reduced from {requestedDots} to {appliedDots} dots)";
    }

    public void PrintCalibrationLabel(HotfolderConfiguration settings)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(settings.PhysicalPrinterQueue))
            throw new InvalidOperationException("Select the physical Zebra printer first.");
        var dpi = settings.PrintResolutionDpi;
        var width = dpi * 4;
        var height = dpi * 4;
        var margin = Math.Max(16, (int)Math.Round(dpi * 0.16));
        var fontLarge = Math.Max(34, (int)Math.Round(dpi * 0.22));
        var fontSmall = Math.Max(22, (int)Math.Round(dpi * 0.13));
        var module = dpi >= 600 ? 5 : dpi >= 300 ? 3 : 2;
        var darkness = settings.PrintDarkness.ToString("0.0", CultureInfo.InvariantCulture);
        var speed = settings.PrintSpeedIps.ToString("0.#", CultureInfo.InvariantCulture);
        var zpl = new StringBuilder()
            .Append("~SD").Append(darkness).Append("\n^XA\n^PR").Append(speed)
            .Append("\n^PW").Append(width).Append("\n^LL").Append(height)
            .Append("\n^FO").Append(margin).Append(',').Append(margin)
            .Append("^A0N,").Append(fontLarge).Append(',').Append(fontLarge)
            .Append("^FDReLabel Quality Test^FS")
            .Append("\n^FO").Append(margin).Append(',').Append(margin + fontLarge + 12)
            .Append("^A0N,").Append(fontSmall).Append(',').Append(fontSmall)
            .Append("^FD").Append(dpi).Append(" DPI  Darkness ").Append(darkness)
            .Append("  Speed ").Append(speed).Append(" ips^FS")
            .Append("\n^FO").Append(margin).Append(',').Append(margin + fontLarge + fontSmall + 42)
            .Append("^GB").Append(width - (2 * margin)).Append(",2,2^FS")
            .Append("\n^FO").Append(margin).Append(',').Append(margin + fontLarge + fontSmall + 62)
            .Append("^BY").Append(module).Append(",2,").Append((int)Math.Round(dpi * 0.55))
            .Append("^BCN,").Append((int)Math.Round(dpi * 0.55)).Append(",Y,N,N^FD123456789012^FS")
            .Append("\n^FO").Append(margin).Append(',').Append((int)Math.Round(height * 0.58))
            .Append("^BQN,2,").Append(dpi >= 600 ? 8 : dpi >= 300 ? 6 : 4)
            .Append("^FDLA,RELABEL-QUALITY-TEST^FS")
            .Append("\n^FO").Append((int)Math.Round(width * 0.48)).Append(',').Append((int)Math.Round(height * 0.62))
            .Append("^A0N,").Append(fontSmall).Append(',').Append(fontSmall)
            .Append("^FDThin lines:^FS")
            .Append("\n^FO").Append((int)Math.Round(width * 0.48)).Append(',').Append((int)Math.Round(height * 0.69))
            .Append("^GB").Append((int)Math.Round(width * 0.40)).Append(",1,1^FS")
            .Append("\n^FO").Append((int)Math.Round(width * 0.48)).Append(',').Append((int)Math.Round(height * 0.73))
            .Append("^GB").Append((int)Math.Round(width * 0.40)).Append(",2,2^FS")
            .Append("\n^FO").Append((int)Math.Round(width * 0.48)).Append(',').Append((int)Math.Round(height * 0.77))
            .Append("^GB").Append((int)Math.Round(width * 0.40)).Append(",3,3^FS")
            .Append("\n^XZ\n").ToString();
        _printer.Print(settings.PhysicalPrinterQueue, "ReLabel - Print Quality Test",
            Encoding.ASCII.GetBytes(zpl));
        _audit.Write("QualityTest", null, null, "Success",
            $"Dpi={dpi}; Quality={settings.PrintQuality}; LabelDarkness={darkness}; Speed={speed}");
        Report("Printed the ReLabel quality test");
    }

    public void DeletePrinted(Guid id)
    {
        var record = _catalog.Find(id) ?? throw new InvalidOperationException("The selected label no longer exists.");
        if (!record.PrintedAt.HasValue)
            throw new InvalidOperationException("Only printed labels can be deleted from this list.");

        DeletePrintedFile(record);
        if (_catalog.Remove(id))
        {
            InvalidatePrepared(id);
            DeleteArchiveFile(record);
            _audit.Write("Delete", record.Id, record.OriginalFileName, "Success", "Manual printed-label deletion");
            CatalogChanged?.Invoke(this, EventArgs.Empty);
            Report($"Deleted printed label {record.OriginalFileName}");
        }
    }

    public void UpdateLabelSettings(Guid id, LabelFitMode fitMode, int widthInches, int heightInches,
        LabelRotation rotation, RotatedBarcodeCompensation barcodeCompensation = RotatedBarcodeCompensation.Off,
        TextEnhancement textEnhancement = TextEnhancement.Off, int fromAddressScalePercent = 158,
        int toAddressScalePercent = 158)
    {
        if ((widthInches, heightInches) is not ((4, 4) or (4, 6) or (4, 8)))
            throw new InvalidOperationException("Select a supported label size.");
        if (!Enum.IsDefined(rotation))
            throw new InvalidOperationException("Select a supported label rotation.");
        if (!Enum.IsDefined(barcodeCompensation))
            throw new InvalidOperationException("Select a supported barcode compensation.");
        if (!Enum.IsDefined(textEnhancement))
            throw new InvalidOperationException("Select a supported text enhancement.");
        if (fromAddressScalePercent is < 100 or > 450)
            throw new InvalidOperationException("FROM address font scale must be between 100% and 450%.");
        if (toAddressScalePercent is < 100 or > 250)
            throw new InvalidOperationException("TO address font scale must be between 100% and 250%.");
        var record = _catalog.Find(id) ?? throw new InvalidOperationException("The selected label no longer exists.");
        InvalidatePrepared(id);
        Update(record with
        {
            FitMode = fitMode,
            OutputWidthInches = widthInches,
            OutputHeightInches = heightInches,
            Rotation = rotation,
            BarcodeCompensation = barcodeCompensation,
            TextEnhancement = textEnhancement,
            FromAddressScalePercent = fromAddressScalePercent,
            ToAddressScalePercent = toAddressScalePercent
        });
        _audit.Write("LabelSettings", record.Id, record.OriginalFileName, "Success",
            $"Fit={fitMode}; Size={widthInches}x{heightInches}; Rotation={(int)rotation}; " +
            $"BarcodeCompensation={barcodeCompensation}; TextEnhancement={textEnhancement}; " +
            $"FromAddressScalePercent={fromAddressScalePercent}; ToAddressScalePercent={toAddressScalePercent}");
    }

    public void UpdateTemplateSelection(Guid id, TemplateSelectionMode mode, Guid? templateId)
    {
        var record = _catalog.Find(id) ?? throw new InvalidOperationException("The selected label no longer exists.");
        AppliedTemplateSnapshot? snapshot = null;
        if (mode == TemplateSelectionMode.Specific)
        {
            if (!templateId.HasValue) throw new InvalidOperationException("Select a carrier template.");
            var match = _templateEngine.Match(_processor.Inspect(record.ArchivePath),
                _templateStore.GetAll(), templateId);
            snapshot = match.Snapshot ?? throw new InvalidOperationException(
                "The selected template cannot be applied to this PDF.");
        }
        else if (mode == TemplateSelectionMode.Auto)
            snapshot = _templateEngine.Match(_processor.Inspect(record.ArchivePath), _templateStore.GetAll()).Snapshot;
        InvalidatePrepared(id);
        Update(record with { TemplateSelection = mode, SelectedTemplateId = templateId,
            AppliedTemplate = snapshot });
        _audit.Write("TemplateOverride", id, record.OriginalFileName, "Success",
            $"Mode={mode}; Template={(snapshot is null ? "None" : snapshot.TemplateId)}; " +
            $"Revision={snapshot?.Revision ?? 0}; Score={snapshot?.MatchScore ?? 0:0.000}");
    }

    public CarrierTemplate CreateTemplate(string sourcePdf, string carrier, string layoutName)
    {
        var id = Guid.NewGuid();
        var stored = _templateStore.StoreSource(id, sourcePdf);
        try
        {
            var template = _templateEngine.BuildTemplate(id, carrier, layoutName, stored, _processor.Inspect(stored));
            _audit.Write("TemplateCreate", null, Path.GetFileName(sourcePdf), "Success",
                $"Template={id}; Carrier={carrier}; Revision=1; DraftZones={template.Zones.Count}");
            return template;
        }
        catch { _templateStore.Delete(id); throw; }
    }

    public CarrierTemplate SaveTemplate(CarrierTemplate template, bool reapplyReady, bool incrementRevision = true)
    {
        var existing = _templateStore.Find(template.Id);
        var saved = template with { Revision = existing is null || !incrementRevision ? template.Revision : existing.Revision + 1,
            LastModified = DateTimeOffset.Now };
        _templateStore.Save(saved);
        var reapplied = 0;
        if (reapplyReady)
        {
            foreach (var record in _catalog.GetAll().Where(x => !x.PrintedAt.HasValue &&
                         x.TemplateSelection != TemplateSelectionMode.None &&
                         x.AppliedTemplate?.TemplateId == saved.Id))
            {
                var match = _templateEngine.Match(_processor.Inspect(record.ArchivePath), [saved], saved.Id);
                if (match.Snapshot is null) continue;
                InvalidatePrepared(record.Id);
                Update(record with { AppliedTemplate = match.Snapshot }); reapplied++;
            }
        }
        _audit.Write("TemplateEdit", null, null, "Success",
            $"Template={saved.Id}; Revision={saved.Revision}; ReappliedReady={reapplied}");
        return saved;
    }

    public CarrierTemplate DuplicateTemplate(Guid id, string? replacementSourcePdf = null)
    {
        CarrierTemplate? result = null;
        try
        {
            result = _templateStore.Duplicate(id, replacementSourcePdf);
            if (!string.IsNullOrWhiteSpace(replacementSourcePdf))
            {
                var inspection = _processor.Inspect(result.SourcePdfPath);
                var page = CarrierTemplateEngine.NormalizePortrait(inspection.SourcePage);
                if (inspection.PageCount != 1 || !CarrierTemplateEngine.IsFourBySix(page))
                    throw new InvalidOperationException(
                        "The replacement sample must be a genuine single-page 4 x 6 PDF.");
                result = result with
                {
                    Features = _templateEngine.ExtractFeatures(
                        page, inspection.RecognizedTsv, result.Zones),
                    LastModified = DateTimeOffset.Now
                };
                _templateStore.Save(result);
            }
            _audit.Write("TemplateDuplicate", null,
                string.IsNullOrWhiteSpace(replacementSourcePdf)
                    ? null : Path.GetFileName(replacementSourcePdf),
                "Success",
                $"Source={id}; Template={result.Id}; ReplacedSample={!string.IsNullOrWhiteSpace(replacementSourcePdf)}");
            return result;
        }
        catch (Exception ex)
        {
            if (result is not null) _templateStore.Delete(result.Id);
            _audit.Write("TemplateDuplicate", null,
                string.IsNullOrWhiteSpace(replacementSourcePdf)
                    ? null : Path.GetFileName(replacementSourcePdf),
                "Failed", $"Source={id}; Error={ex.GetType().Name}");
            throw;
        }
    }

    public void ExportTemplate(Guid id, string destinationPath)
    {
        try
        {
            _templateStore.ExportPackage(id, destinationPath);
            _audit.Write("TemplateExport", null, Path.GetFileName(destinationPath), "Success",
                $"Template={id}");
        }
        catch (Exception ex)
        {
            _audit.Write("TemplateExport", null, Path.GetFileName(destinationPath), "Failed",
                $"Template={id}; Error={ex.GetType().Name}");
            throw;
        }
    }

    public CarrierTemplate ImportTemplate(string packagePath)
    {
        CarrierTemplate? imported = null;
        try
        {
            imported = _templateStore.ImportPackage(packagePath);
            _ = _processor.Inspect(imported.SourcePdfPath);
            _audit.Write("TemplateImport", null, Path.GetFileName(packagePath), "Success",
                $"Template={imported.Id}; Carrier={imported.Carrier}; Revision={imported.Revision}");
            return imported;
        }
        catch (Exception ex)
        {
            if (imported is not null) _templateStore.Delete(imported.Id);
            _audit.Write("TemplateImport", null, Path.GetFileName(packagePath), "Failed",
                $"Error={ex.GetType().Name}");
            var message = ex is InvalidDataException
                ? ex.Message
                : "The template package could not be imported. It may be damaged or contain an unreadable source PDF.";
            throw new InvalidDataException(message, ex);
        }
    }

    public void SetTemplateEnabled(Guid id, bool enabled)
    {
        var template = _templateStore.Find(id) ?? throw new InvalidOperationException("Template no longer exists.");
        _templateStore.Save(template with { Enabled = enabled, LastModified = DateTimeOffset.Now });
        _audit.Write("TemplateEnabled", null, null, "Success", $"Template={id}; Enabled={enabled}");
    }

    public void DeleteTemplate(Guid id)
    {
        _templateStore.Delete(id);
        _audit.Write("TemplateDelete", null, null, "Success", $"Template={id}");
    }

    public PdfInspection InspectPdf(string path) => _processor.Inspect(path);
    public CarrierTemplateEngine TemplateEngine => _templateEngine;
    public void AuditTemplateTest(Guid id, double score, TemplateMatchDisposition disposition,
        bool barcodeVerified)
    {
        _audit.Write("TemplateTest", null, null, barcodeVerified ? "Success" : "Warning",
            $"Template={id}; Score={score:0.000}; Match={disposition}; BarcodeVerified={barcodeVerified}");
    }

    public int PurgeExpiredPrinted(DateTimeOffset now)
    {
        var expired = _catalog.GetAll()
            .Where(record => record.PrintedAt.HasValue && record.PrintedAt.Value <= now.AddHours(-48))
            .ToArray();
        var removed = 0;
        foreach (var record in expired)
        {
            try
            {
                DeletePrintedFile(record);
                if (_catalog.Remove(record.Id))
                {
                    InvalidatePrepared(record.Id);
                    DeleteArchiveFile(record);
                    _audit.Write("Delete", record.Id, record.OriginalFileName, "Success", "Automatic 48-hour retention");
                    removed++;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (removed > 0)
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
            Report($"Removed {removed} printed label{(removed == 1 ? string.Empty : "s")} older than 48 hours");
        }
        return removed;
    }

    private void DeletePrintedFile(LabelRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.PrintedPath) || !File.Exists(record.PrintedPath))
            return;
        if (!PrintedFileMover.IsInside(record.PrintedPath, _configuration.PrintedFolder))
            throw new InvalidOperationException("ReLabel will not delete a PDF outside the configured Printed folder.");
        MarkInternalFileChange(record.PrintedPath);
        File.Delete(record.PrintedPath);
        RemoveEmptyPrintedParents(Path.GetDirectoryName(record.PrintedPath));
    }

    private void DeleteArchiveFile(LabelRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ArchivePath) || !File.Exists(record.ArchivePath)) return;
        if (!PrintedFileMover.IsInside(record.ArchivePath, _configuration.ArchiveFolder))
            throw new InvalidOperationException("ReLabel will not delete a PDF outside the configured Archive folder.");
        File.Delete(record.ArchivePath);
    }

    private void RemoveEmptyPrintedParents(string? folder)
    {
        var root = Path.GetFullPath(_configuration.PrintedFolder).TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(folder) && PrintedFileMover.IsInside(folder + Path.DirectorySeparatorChar, root) &&
               !string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), root,
                   StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFileSystemEntries(folder).Any()) break;
            Directory.Delete(folder);
            folder = Path.GetDirectoryName(folder);
        }
    }

    private int QueueExistingFiles()
    {
        var count = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(_configuration.IncomingFolder, "*.pdf", SearchOption.TopDirectoryOnly))
            {
                QueueFile(path);
                count++;
            }
        }
        catch (IOException exception)
        {
            Report($"Unable to scan incoming folder: {exception.Message}");
        }
        return count;
    }

    private void BackfillMissingAddresses(CancellationToken cancellationToken)
    {
        var missing = _catalog.GetAll()
            .Where(record => string.IsNullOrWhiteSpace(record.ShipToAddress) && File.Exists(record.ArchivePath))
            .ToArray();
        if (missing.Length == 0) return;
        Report($"Indexing addresses for {missing.Length} existing label{(missing.Length == 1 ? string.Empty : "s")}...");
        foreach (var record in missing)
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                var inspection = _processor.Inspect(record.ArchivePath);
                var address = DestinationAddressParser.Parse(inspection.RecognizedText, inspection.RecognizedTsv,
                    record.ShipToName);
                if (!string.IsNullOrWhiteSpace(address))
                    Update(record with { ShipToAddress = address });
            }
            catch { /* A missing address remains clearly marked in the list. */ }
        }
        Report("No new labels found");
    }

    private void RecoverLegacyMultiPageLabels()
    {
        var legacyFailures = _catalog.GetAll()
            .Where(record => record.Status == LabelStatus.Failed &&
                record.StatusMessage?.Contains("Expected one label page", StringComparison.OrdinalIgnoreCase) == true &&
                File.Exists(record.ArchivePath))
            .ToArray();

        foreach (var record in legacyFailures)
        {
            try
            {
                var inspection = _processor.Inspect(record.ArchivePath);
                if (inspection.PageCount <= 1) continue;

                var shipTo = RecipientNameParser.Parse(inspection.RecognizedText, inspection.RecognizedTsv);
                var address = DestinationAddressParser.Parse(inspection.RecognizedText, inspection.RecognizedTsv, shipTo);
                var recovered = record with
                {
                    PageCount = inspection.PageCount,
                    ShipToName = shipTo,
                    ShipToAddress = address,
                    AppliedTemplate = _templateEngine.Match(inspection, _templateStore.GetAll()).Snapshot,
                    Status = shipTo == "Unknown recipient" ? LabelStatus.Warning : LabelStatus.Ready,
                    StatusMessage = shipTo == "Unknown recipient"
                        ? $"Recipient was not recognized; all {inspection.PageCount} labels can still be previewed and printed."
                        : $"Ready to preview or print {inspection.PageCount} labels"
                };
                _catalog.Update(recovered);
                _processor.RememberInspection(record.ArchivePath, inspection);
                _audit.Write("ImportRecovery", record.Id, record.OriginalFileName, "Success",
                    $"Enabled multi-page PDF support; Pages={inspection.PageCount}");
            }
            catch (Exception exception)
            {
                _audit.Write("ImportRecovery", record.Id, record.OriginalFileName, "Failed", exception.Message);
            }
        }
    }

    private void OnFileAvailable(object sender, FileSystemEventArgs arguments) => QueueFile(arguments.FullPath);
    private void OnFileRenamed(object sender, RenamedEventArgs arguments) => QueueFile(arguments.FullPath);

    private void OnManagedFileDeleted(object sender, FileSystemEventArgs arguments) =>
        HandleExternalManagedFileRemoval(arguments.FullPath, "External file deletion");

    private void OnManagedFileRenamed(object sender, RenamedEventArgs arguments)
    {
        if (ConsumeInternalFileChange(arguments.OldFullPath) || ConsumeInternalFileChange(arguments.FullPath)) return;
        HandleExternalManagedFileRemoval(arguments.OldFullPath, $"External file rename to {Path.GetFileName(arguments.FullPath)}");
    }

    private void HandleExternalManagedFileRemoval(string path, string reason)
    {
        if (ConsumeInternalFileChange(path)) return;
        var record = _catalog.GetAll().FirstOrDefault(item =>
            PathEquals(item.SourcePath, path) || PathEquals(item.PrintedPath, path));
        if (record is null) return;
        RemoveManagedRecord(record, reason);
    }

    public bool ReconcileManagedFiles()
    {
        var changed = false;
        foreach (var record in _catalog.GetAll())
        {
            var managedPath = record.PrintedAt.HasValue ? record.PrintedPath : record.SourcePath;
            if (string.IsNullOrWhiteSpace(managedPath) || File.Exists(managedPath)) continue;
            changed |= RemoveManagedRecord(record, "Startup/folder reconciliation");
        }
        return changed;
    }

    private bool RemoveManagedRecord(LabelRecord record, string reason)
    {
        try
        {
            DeleteArchiveFile(record);
            if (_catalog.Remove(record.Id))
            {
                InvalidatePrepared(record.Id);
                _audit.Write("Delete", record.Id, record.OriginalFileName, "Success", reason);
                CatalogChanged?.Invoke(this, EventArgs.Empty);
                Report($"Removed {record.OriginalFileName} after its managed PDF was deleted");
                return true;
            }
        }
        catch (Exception exception)
        {
            _audit.Write("Delete", record.Id, record.OriginalFileName, "Failed", $"{reason}: {exception.Message}");
        }
        return false;
    }

    private void MigrateLegacyReadyFiles()
    {
        foreach (var record in _catalog.GetAll().Where(item => !item.PrintedAt.HasValue))
        {
            if (string.IsNullOrWhiteSpace(record.SourcePath) || !File.Exists(record.SourcePath) ||
                !PrintedFileMover.IsInside(record.SourcePath, _configuration.IncomingFolder)) continue;
            try
            {
                var readyPath = ManagedFileNames.NextAvailable(_configuration.ReadyFolder, record.OriginalFileName);
                File.Move(record.SourcePath, readyPath);
                var migrated = record with { SourcePath = readyPath, OriginalFileName = Path.GetFileName(readyPath) };
                Update(migrated);
                _audit.Write("ImportMigration", record.Id, migrated.OriginalFileName, "Success", "Moved legacy Incoming file to Ready");
            }
            catch (Exception exception)
            {
                _audit.Write("ImportMigration", record.Id, record.OriginalFileName, "Failed", exception.Message);
            }
        }
    }

    private async Task ProcessIncomingAsync()
    {
        await foreach (var path in _incoming.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
        {
            try
            {
                if (!await WaitForCompleteFileAsync(path, _stopping.Token).ConfigureAwait(false))
                {
                    Report($"Skipped incomplete file: {Path.GetFileName(path)}");
                    continue;
                }
                await ImportAsync(path, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _audit.Write("Import", null, Path.GetFileName(path), "Failed", exception.Message);
                Report($"Import failed for {Path.GetFileName(path)}: {exception.Message}");
            }
            finally
            {
                _queued.TryRemove(path, out _);
            }
        }
    }

    private async Task ImportAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var incomingInfo = new FileInfo(sourcePath);
        var fingerprint = $"{incomingInfo.FullName.ToUpperInvariant()}|{incomingInfo.Length}|{incomingInfo.LastWriteTimeUtc.Ticks}|{Guid.NewGuid():N}";

        var readyPath = ManagedFileNames.NextAvailable(_configuration.ReadyFolder, incomingInfo.Name);
        File.Move(sourcePath, readyPath);
        var info = new FileInfo(readyPath);

        Report($"Importing {info.Name}...");
        PdfInspection? inspection = null;
        string shipTo;
        string shipToAddress = string.Empty;
        string? inspectionError = null;
        try
        {
            inspection = await Task.Run(() => _processor.Inspect(readyPath), cancellationToken).ConfigureAwait(false);
            shipTo = RecipientNameParser.Parse(inspection.RecognizedText, inspection.RecognizedTsv);
            shipToAddress = DestinationAddressParser.Parse(inspection.RecognizedText, inspection.RecognizedTsv, shipTo);
        }
        catch (Exception exception)
        {
            shipTo = "Unknown recipient";
            inspectionError = exception.Message;
        }

        var archivePath = CreateArchivePath(_configuration.ArchiveFolder, shipTo, info.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        File.Copy(readyPath, archivePath, overwrite: false);
        if (inspection is not null)
            _processor.RememberInspection(archivePath, inspection);

        var record = new LabelRecord
        {
            ShipToName = shipTo,
            ShipToAddress = shipToAddress,
            OriginalFileName = info.Name,
            SourcePath = readyPath,
            ArchivePath = archivePath,
            SourceFingerprint = fingerprint,
            PageCount = inspection?.PageCount ?? 1,
            FitMode = _configuration.DefaultFitMode,
            OutputWidthInches = _configuration.DefaultLabelWidthInches,
            OutputHeightInches = _configuration.DefaultLabelHeightInches,
            Rotation = _configuration.DefaultRotation,
            BarcodeCompensation = _configuration.DefaultBarcodeCompensation,
            TextEnhancement = _configuration.DefaultTextEnhancement,
            FromAddressScalePercent = _configuration.EffectiveDefaultFromAddressScalePercent,
            ToAddressScalePercent = _configuration.EffectiveDefaultToAddressScalePercent,
            AppliedTemplate = inspection is not null
                ? _templateEngine.Match(inspection, _templateStore.GetAll()).Snapshot : null,
            Status = inspectionError is not null ? LabelStatus.Failed
                : shipTo == "Unknown recipient" ? LabelStatus.Warning : LabelStatus.Ready,
            StatusMessage = inspectionError ?? (shipTo == "Unknown recipient"
                ? "Recipient was not recognized; the label can still be previewed and printed."
                : inspection!.PageCount > 1
                    ? $"Ready to preview or print {inspection.PageCount} labels"
                    : "Ready to preview or print")
        };
        _catalog.Add(record);
        _audit.Write("Import", record.Id, record.OriginalFileName, inspectionError is null ? "Success" : "Warning",
            inspectionError ?? $"Moved from Incoming to Ready; Pages={record.PageCount}");
        if (record.AppliedTemplate is not null)
            _audit.Write("TemplateMatch", record.Id, record.OriginalFileName,
                record.AppliedTemplate.MatchDisposition.ToString(),
                $"Template={record.AppliedTemplate.TemplateId}; Revision={record.AppliedTemplate.Revision}; " +
                $"Score={record.AppliedTemplate.MatchScore:0.000}");
        CatalogChanged?.Invoke(this, EventArgs.Empty);
        Report("No new labels found");
        if (inspectionError is null)
            WarmPrintCache(record);

        if (_configuration.AutoPrint && inspectionError is null)
        {
            try { await PrintAsync(record.Id, cancellationToken,
                record.AppliedTemplate?.MatchDisposition == TemplateMatchDisposition.Ambiguous).ConfigureAwait(false); }
            catch { /* The failed state is already recorded for the user. */ }
        }
    }

    private static async Task<bool> WaitForCompleteFileAsync(string path, CancellationToken cancellationToken)
    {
        long previousLength = -1;
        DateTime previousWrite = DateTime.MinValue;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 0 && info.Length == previousLength && info.LastWriteTimeUtc == previousWrite)
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    return stream.Length > 0;
                }
                if (info.Exists)
                {
                    previousLength = info.Length;
                    previousWrite = info.LastWriteTimeUtc;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static string CreateArchivePath(string root, string shipTo, string originalName)
    {
        var safeRecipient = Sanitize(shipTo);
        var safeFile = Sanitize(Path.GetFileNameWithoutExtension(originalName));
        var now = DateTime.Now;
        var folder = Path.Combine(root, now.ToString("yyyy"), now.ToString("MM"), safeRecipient);
        return Path.Combine(folder, $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..32] + $"-{safeFile}.pdf");
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "Unknown recipient" : safe[..Math.Min(safe.Length, 60)];
    }

    private void Update(LabelRecord record)
    {
        _catalog.Update(record);
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkInternalFileChange(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) _internalFileChanges[Path.GetFullPath(path)] = DateTimeOffset.UtcNow;
    }

    private bool ConsumeInternalFileChange(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var fullPath = Path.GetFullPath(path);
        if (!_internalFileChanges.TryRemove(fullPath, out var timestamp)) return false;
        return timestamp > DateTimeOffset.UtcNow.AddMinutes(-1);
    }

    private static bool PathEquals(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private void DisposeWatchers()
    {
        _incomingWatcher?.Dispose();
        _readyWatcher?.Dispose();
        _printedWatcher?.Dispose();
        _incomingWatcher = null;
        _readyWatcher = null;
        _printedWatcher = null;
    }

    private void Report(string message) => ActivityChanged?.Invoke(this, message);

    private async Task<CachedPreparedLabel> GetPreparedAsync(LabelRecord record, bool applyRotation,
        CancellationToken cancellationToken, bool ignoreTemplate = false)
    {
        var key = PreparationKey(record, applyRotation, ignoreTemplate);
        var existed = _preparedCache.TryGetValue(key, out var existing);
        var configuration = _configuration;
        var lazy = existing ?? _preparedCache.GetOrAdd(key, _ => new Lazy<Task<CachedPreparedLabel>>(() => Task.Run(() =>
        {
            var timer = Stopwatch.StartNew();
            if (_diskCache.TryGet(record.Id, key, out var cached))
                return new CachedPreparedLabel(cached, "Disk", timer.ElapsedMilliseconds);
            var prepared = _processor.Prepare(record.ArchivePath, configuration.PrintResolutionDpi,
                configuration.MarginInches, record.OutputWidthInches, record.OutputHeightInches, record.FitMode,
                applyRotation ? record.Rotation : LabelRotation.None, configuration.PrintQuality,
                configuration.PrintDarkness, configuration.PrintSpeedIps,
                applyRotation ? record.BarcodeCompensation : RotatedBarcodeCompensation.Off,
                record.TextEnhancement, record.EffectiveFromAddressScalePercent,
                record.EffectiveToAddressScalePercent, ignoreTemplate ? null : record.AppliedTemplate);
            _diskCache.Put(record.Id, key, prepared);
            return new CachedPreparedLabel(prepared, "Miss", timer.ElapsedMilliseconds);
        }), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var prepared = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            TrimPreparedCache(key);
            return existed ? prepared with { Source = "Memory", DurationMs = 0 } : prepared;
        }
        catch when (lazy.IsValueCreated && lazy.Value.IsFaulted)
        {
            _preparedCache.TryRemove(key, out _);
            throw;
        }
    }

    private string PreparationKey(LabelRecord record, bool applyRotation, bool ignoreTemplate = false)
    {
        var info = new FileInfo(record.ArchivePath);
        var rotation = applyRotation ? record.Rotation : LabelRotation.None;
        var compensation = applyRotation ? record.BarcodeCompensation : RotatedBarcodeCompensation.Off;
        return string.Join('|', record.Id, info.Exists ? info.Length : 0,
            info.Exists ? info.LastWriteTimeUtc.Ticks : 0, _configuration.PrintResolutionDpi,
            _configuration.MarginInches, _configuration.PrintQuality, _configuration.PrintDarkness,
            _configuration.PrintSpeedIps, record.OutputWidthInches, record.OutputHeightInches,
            record.FitMode, rotation, compensation, record.TextEnhancement,
            record.EffectiveFromAddressScalePercent, record.EffectiveToAddressScalePercent,
            ignoreTemplate ? "no-template" : record.AppliedTemplate?.Hash ?? "no-template", "cache-v6");
    }

    private void TrimPreparedCache(string currentKey)
    {
        if (_preparedCache.Count <= 8) return;
        foreach (var key in _preparedCache.Keys.Where(key => key != currentKey).Take(_preparedCache.Count - 8))
            _preparedCache.TryRemove(key, out _);
    }

    private void InvalidatePrepared(Guid id)
    {
        var prefix = id + "|";
        foreach (var key in _preparedCache.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
            _preparedCache.TryRemove(key, out _);
        _diskCache.Remove(id);
    }

    public async Task<bool> VerifyPrintedBarcodeAsync(Guid id, string scannedValue,
        CancellationToken cancellationToken = default)
    {
        var record = _catalog.Find(id) ?? throw new InvalidOperationException("The printed label no longer exists.");
        if (!record.PrintedAt.HasValue) throw new InvalidOperationException("Print the label before verifying it.");
        var scanned = scannedValue.Trim().Trim('\r', '\n');
        if (scanned.Length == 0) throw new InvalidOperationException("Scan a barcode before selecting Verify.");
        var prepared = (await GetPreparedAsync(record, true, cancellationToken).ConfigureAwait(false)).Label;
        var payloads = record.PageCount > 1
            ? _processor.DetectBarcodePayloads(record.ArchivePath)
            : new BarcodeInspector().Detect(prepared.Image).Select(region => region.Value).ToArray();
        var matched = payloads.Any(payload => string.Equals(payload.Trim(), scanned, StringComparison.Ordinal));
        record = record with
        {
            ScanVerification = matched ? ScanVerificationStatus.Verified : ScanVerificationStatus.Mismatch,
            ScanVerifiedAt = matched ? DateTimeOffset.Now : null,
            StatusMessage = matched ? "Printed and scan-verified" : "Printed; scanned barcode did not match"
        };
        Update(record);
        _audit.Write("ScanVerification", record.Id, record.OriginalFileName,
            matched ? "Success" : "Mismatch", $"DecodedCandidates={payloads.Count}; PayloadLogged=False");
        return matched;
    }

    private async Task PreloadReadyLabelsAsync(CancellationToken cancellationToken)
    {
        foreach (var record in _catalog.GetAll().Where(item => !item.PrintedAt.HasValue)
                     .OrderByDescending(item => item.ImportedAt).Take(3))
        {
            try
            {
                var result = await GetPreparedAsync(record, true, cancellationToken).ConfigureAwait(false);
                _audit.Write("Performance", record.Id, record.OriginalFileName, "Startup preload",
                    $"Cache={result.Source}; PrepareMs={result.DurationMs}");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception)
            {
                _audit.Write("Performance", record.Id, record.OriginalFileName, "Preload failed", exception.Message);
            }
        }
    }

    private void RefreshPrinterStatus()
    {
        if (_printerStatusProvider is null || Interlocked.Exchange(ref _printerStatusCheckActive, 1) != 0) return;
        try
        {
            var status = _printerStatusProvider.GetStatus(_configuration.PhysicalPrinterQueue);
            PrinterStatusChanged?.Invoke(this, status);
        }
        catch (Exception exception)
        {
            PrinterStatusChanged?.Invoke(this,
                new PrinterStatusSnapshot(PrinterAvailability.Unknown, $"Status unavailable: {exception.Message}"));
        }
        finally { Volatile.Write(ref _printerStatusCheckActive, 0); }
    }

    private void ReportPrintProgress(LabelRecord record, string message)
    {
        PrintProgressChanged?.Invoke(this, new PrintProgressEventArgs(record.Id, record.OriginalFileName, message));
        Report($"{message.TrimEnd('.')} {record.OriginalFileName}");
    }

    public void Dispose()
    {
        DisposeWatchers();
        _stopping.Cancel();
        _retentionTimer.Dispose();
        _printerStatusTimer.Dispose();
        _incoming.Writer.TryComplete();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _stopping.Dispose();
        _preparedCache.Clear();
        _printGate.Dispose();
    }
}

public sealed record PrintProgressEventArgs(Guid LabelId, string FileName, string Message);
internal sealed record CachedPreparedLabel(PreparedLabel Label, string Source, long DurationMs);
