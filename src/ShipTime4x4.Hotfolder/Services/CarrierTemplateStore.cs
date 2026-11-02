using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.Services;

public sealed class CarrierTemplateStore
{
    private sealed record Catalog(int Version, IReadOnlyList<CarrierTemplate> Templates);
    private sealed record PortablePackage(int FormatVersion, DateTimeOffset ExportedUtc,
        CarrierTemplate? Template);
    private const int PortableFormatVersion = 1;
    private const long MaximumManifestBytes = 2 * 1024 * 1024;
    private const long MaximumSourcePdfBytes = 100 * 1024 * 1024;
    private const string ManifestEntryName = "template.json";
    private const string SourceEntryName = "sample.pdf";
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _catalogPath;
    private List<CarrierTemplate> _templates;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true
    };

    public CarrierTemplateStore(string dataFolder)
    {
        _root = Path.Combine(dataFolder, "CarrierTemplates");
        _catalogPath = Path.Combine(_root, "templates.json");
        Directory.CreateDirectory(_root);
        _templates = Load();
    }

    public IReadOnlyList<CarrierTemplate> GetAll()
    {
        lock (_gate) return _templates.OrderBy(x => x.Carrier).ThenBy(x => x.LayoutName).ToArray();
    }
    public CarrierTemplate? Find(Guid id) { lock (_gate) return _templates.FirstOrDefault(x => x.Id == id); }

    public string StoreSource(Guid id, string sourcePdf)
    {
        var folder = Path.Combine(_root, id.ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "sample.pdf");
        File.Copy(sourcePdf, destination, true);
        return destination;
    }

    public void Save(CarrierTemplate template)
    {
        CarrierTemplateValidator.Validate(template);
        lock (_gate)
        {
            var previous = _templates.ToList();
            var index = _templates.FindIndex(x => x.Id == template.Id);
            if (index < 0) _templates.Add(template); else _templates[index] = template;
            try { SaveLocked(); }
            catch { _templates = previous; throw; }
        }
    }

    public CarrierTemplate Duplicate(Guid id, string? replacementSourcePdf = null)
    {
        var source = Find(id) ?? throw new InvalidOperationException("Template no longer exists.");
        var sourcePdf = string.IsNullOrWhiteSpace(replacementSourcePdf)
            ? source.SourcePdfPath : replacementSourcePdf;
        if (!File.Exists(sourcePdf))
            throw new FileNotFoundException("The replacement sample PDF no longer exists.", sourcePdf);
        var duplicateId = Guid.NewGuid();
        var pdf = StoreSource(duplicateId, sourcePdf);
        var copy = source with { Id = duplicateId, Revision = 1, LayoutName = source.LayoutName + " copy",
            SourcePdfPath = pdf, LastModified = DateTimeOffset.Now,
            Zones = source.Zones.Select(zone => zone with { Id = Guid.NewGuid() }).ToArray() };
        try
        {
            Save(copy);
            return copy;
        }
        catch
        {
            Delete(duplicateId);
            throw;
        }
    }

    public void ExportPackage(Guid id, string destinationPath)
    {
        var template = Find(id) ?? throw new InvalidOperationException("Template no longer exists.");
        CarrierTemplateValidator.Validate(template);
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("Choose an existing folder for the exported template.");

        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var portable = template with { SourcePdfPath = SourceEntryName };
                var package = new PortablePackage(PortableFormatVersion, DateTimeOffset.UtcNow, portable);
                var manifest = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
                    writer.Write(JsonSerializer.Serialize(package, Options));

                var source = archive.CreateEntry(SourceEntryName, CompressionLevel.Optimal);
                using var sourceOutput = source.Open();
                using var sourceInput = File.OpenRead(template.SourcePdfPath);
                sourceInput.CopyTo(sourceOutput);
            }
            File.Move(temporary, destinationPath, true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public CarrierTemplate ImportPackage(string packagePath,
        Func<CarrierTemplate, CarrierTemplate>? inspectSample = null)
    {
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("The selected template package no longer exists.", packagePath);

        PortablePackage package;
        ZipArchiveEntry sourceEntry;
        using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumSourcePdfBytes + MaximumManifestBytes + 1024 * 1024)
            throw new InvalidDataException("The template package is too large.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count != 2)
            throw new InvalidDataException("A template package must contain only template.json and sample.pdf.");
        var manifests = archive.Entries.Where(entry =>
            string.Equals(entry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var sources = archive.Entries.Where(entry =>
            string.Equals(entry.FullName, SourceEntryName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1 || sources.Length != 1)
            throw new InvalidDataException(
                "This is not a complete ReLabel template package. It must contain template.json and sample.pdf.");
        if (manifests[0].Length <= 0 || manifests[0].Length > MaximumManifestBytes)
            throw new InvalidDataException("The template package manifest is empty or too large.");
        sourceEntry = sources[0];
        if (sourceEntry.Length <= 5 || sourceEntry.Length > MaximumSourcePdfBytes)
            throw new InvalidDataException("The template package source PDF is empty or too large.");
        try
        {
            using var manifestInput = manifests[0].Open();
            using var manifestBytes = new MemoryStream();
            CopyBounded(manifestInput, manifestBytes, MaximumManifestBytes);
            manifestBytes.Position = 0;
            using var reader = new StreamReader(manifestBytes, Encoding.UTF8, true);
            package = JsonSerializer.Deserialize<PortablePackage>(reader.ReadToEnd(), Options)
                ?? throw new InvalidDataException("The template package manifest is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The template package manifest is invalid.", ex);
        }
        if (package.FormatVersion != PortableFormatVersion)
            throw new InvalidDataException(
                $"Template package version {package.FormatVersion} is not supported by this ReLabel version.");
        if (package.Template is null)
            throw new InvalidDataException("The template package contains no template data.");
        CarrierTemplateValidator.ValidateMetadata(package.Template);

        var id = Guid.NewGuid();
        var folder = Path.Combine(_root, id.ToString("N"));
        var storedPdf = Path.Combine(folder, SourceEntryName);
        Directory.CreateDirectory(folder);
        try
        {
            using (var input = sourceEntry.Open())
            using (var output = new FileStream(storedPdf, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                CopyBounded(input, output, MaximumSourcePdfBytes);
            EnsurePdfHeader(storedPdf);

            var imported = package.Template with
            {
                Id = id,
                Revision = Math.Max(1, package.Template.Revision),
                LayoutName = ImportedLayoutName(package.Template.Carrier, package.Template.LayoutName),
                SourcePdfPath = storedPdf,
                LastModified = DateTimeOffset.Now,
                Zones = package.Template.Zones
                    .Select(zone => zone with { Id = Guid.NewGuid() }).ToArray()
            };
            CarrierTemplateValidator.Validate(imported);
            // Inspect and regenerate untrusted matching features BEFORE committing the catalog.
            if (inspectSample is not null) imported = inspectSample(imported);
            Save(imported);
            return imported;
        }
        catch
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            throw;
        }
    }

    internal static void CopyBounded(Stream input, Stream output, long maximumBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            total += count;
            if (total > maximumBytes)
                throw new InvalidDataException("Template archive content exceeds its extraction limit.");
            output.Write(buffer, 0, count);
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            if (_templates.RemoveAll(x => x.Id == id) > 0) SaveLocked();
        }
        var folder = Path.Combine(_root, id.ToString("N"));
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    private List<CarrierTemplate> Load()
    {
        if (!File.Exists(_catalogPath)) return [];
        try
        {
            if (new FileInfo(_catalogPath).Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Template catalog is too large.");
            var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(_catalogPath), Options);
            if (catalog?.Templates is null) throw new InvalidDataException("Template catalog is invalid.");
            var valid = new List<CarrierTemplate>();
            foreach (var template in catalog.Templates)
            {
                try { CarrierTemplateValidator.ValidateMetadata(template); valid.Add(template); }
                catch (InvalidDataException) { }
            }
            if (valid.Count != catalog.Templates.Count) BackupInvalidCatalog();
            return valid;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            BackupInvalidCatalog();
            return [];
        }
    }
    private void BackupInvalidCatalog() => File.Copy(_catalogPath,
        _catalogPath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N"), false);
    private void SaveLocked()
    {
        var temporary = _catalogPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Catalog(2, _templates), Options));
        File.Move(temporary, _catalogPath, true);
    }

    private string ImportedLayoutName(string carrier, string requested)
    {
        var baseName = string.IsNullOrWhiteSpace(requested) ? "Imported layout" : requested.Trim();
        lock (_gate)
        {
            if (!_templates.Any(template =>
                    string.Equals(template.Carrier, carrier, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(template.LayoutName, baseName, StringComparison.OrdinalIgnoreCase)))
                return baseName;
            var candidate = baseName + " imported";
            var number = 2;
            while (_templates.Any(template =>
                       string.Equals(template.Carrier, carrier, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(template.LayoutName, candidate, StringComparison.OrdinalIgnoreCase)))
                candidate = $"{baseName} imported {number++}";
            return candidate;
        }
    }

    private static void EnsurePdfHeader(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[Math.Min(1024, checked((int)stream.Length))];
        _ = stream.Read(header, 0, header.Length);
        if (Encoding.ASCII.GetString(header).IndexOf("%PDF-", StringComparison.Ordinal) < 0)
            throw new InvalidDataException("The template package sample is not a PDF file.");
    }
}

public static class CarrierTemplateValidator
{
    public static void ValidateMetadata(CarrierTemplate? template)
    {
        if (template is null || string.IsNullOrWhiteSpace(template.Carrier) ||
            string.IsNullOrWhiteSpace(template.LayoutName) || template.Carrier.Length > 160 ||
            template.LayoutName.Length > 160 || template.Revision < 1)
            throw new InvalidDataException("Template identity is missing or invalid.");
        if (!double.IsFinite(template.BuiltInAssetScale) || template.BuiltInAssetScale is < .05 or > 20)
            throw new InvalidDataException("Template asset scale is invalid.");
        ValidateZones(template.Zones);
        var f = template.Features;
        if (f is null || f.StaticSignature is null ||
            (f.StaticSignature.Length != 0 && f.StaticSignature.Length != 32 * 48) ||
            f.StaticSignature.Any(c => c is not ('0' or '1' or '?')) ||
            f.Width is < 0 or > 24000 || f.Height is < 0 or > 24000 ||
            f.BarcodeLocations is null || f.BarcodeLocations.Count > 128 ||
            f.BarcodeFormats is null || f.BarcodeFormats.Count > 128 ||
            f.StaticOcrAnchors is null || f.StaticOcrAnchors.Count > 256 ||
            f.BarcodeFormats.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 64) ||
            f.StaticOcrAnchors.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 128))
            throw new InvalidDataException("Template matching metadata is missing or invalid.");
        foreach (var rect in f.BarcodeLocations) ValidateRect(rect, "barcode");
    }

    public static void ValidateZones(IReadOnlyList<TemplateZone>? zones)
    {
        if (zones is null || zones.Count is < 1 or > 500 || zones.Any(z => z is null) ||
            zones.Select(z => z.Id).Distinct().Count() != zones.Count)
            throw new InvalidDataException("Template zones are missing, duplicated, or too numerous.");
        foreach (var zone in zones)
        {
            if (!Enum.IsDefined(zone.Type) || !Enum.IsDefined(zone.OtherPriority) ||
                !Enum.IsDefined(zone.BuiltInAsset) || zone.OtherName is null || zone.OtherName.Length > 160)
                throw new InvalidDataException("A template zone has an invalid type, priority, or name.");
            if (zone.HasSourceRegion) ValidateRect(zone.Source, "source");
            ValidateRect(zone.Destination4x4, "destination");
        }
    }

    public static void Validate(CarrierTemplate template)
    {
        ValidateMetadata(template);
        if (string.IsNullOrWhiteSpace(template.Carrier)) throw new InvalidOperationException("Carrier name is required.");
        if (string.IsNullOrWhiteSpace(template.LayoutName)) throw new InvalidOperationException("Layout name is required.");
        if (!File.Exists(template.SourcePdfPath)) throw new InvalidOperationException("Template source PDF is missing.");
        if (!template.Zones.Any(x => x.Type == TemplateZoneType.ToAddress))
            throw new InvalidOperationException("Mark at least one To Address zone.");
        if (!template.Zones.Any(x => x.Type is TemplateZoneType.CarrierBarcode or TemplateZoneType.ShippingBarcode))
            throw new InvalidOperationException("Mark at least one barcode zone.");
        foreach (var zone in template.Zones)
        {
            if (zone.HasSourceRegion) ValidateRect(zone.Source, "source");
            if (zone.Type == TemplateZoneType.BuiltInAsset &&
                zone.BuiltInAsset == TemplateBuiltInAssetKind.None)
                throw new InvalidOperationException("A built-in asset has no content type.");
            if (!zone.IsPlaced4x4)
                throw new InvalidOperationException($"Place or delete the staged zone '{zone.DisplayName}' before saving.");
            ValidateRect(zone.Destination4x4, "destination");
            var d = zone.Destination4x4;
            const double margin = .03125;
            const double tolerance = .0005;
            if (d.X < margin - tolerance || d.Y < margin - tolerance ||
                d.Right > 1 - margin + tolerance || d.Bottom > 1 - margin + tolerance)
                throw new InvalidOperationException(
                    $"'{zone.DisplayName}' crosses the red 0.125-inch safe-margin outline.");
        }
    }
    private static void ValidateRect(NormalizedRect? rect, string name)
    {
        if (rect is null || !double.IsFinite(rect.X) || !double.IsFinite(rect.Y) ||
            !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height) ||
            rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 || rect.Right > 1 || rect.Bottom > 1)
            throw new InvalidDataException($"A {name} zone is invalid or outside its canvas.");
    }
}
