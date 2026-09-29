using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.Services;

public static class PrintedFileMover
{
    public static string? MoveToPrinted(LabelRecord record, HotfolderConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(record.PrintedPath) && File.Exists(record.PrintedPath))
            return record.PrintedPath;

        var sourcePath = ResolveSourcePath(record, configuration.ReadyFolder);
        if (sourcePath is null || !IsInside(sourcePath, configuration.ReadyFolder))
            return null;

        var now = DateTime.Now;
        var recipient = Sanitize(record.ShipToName);
        var destinationFolder = Path.Combine(configuration.PrintedFolder, now.ToString("yyyy"),
            now.ToString("MM"), recipient);
        Directory.CreateDirectory(destinationFolder);

        var destination = ManagedFileNames.NextAvailable(destinationFolder, record.OriginalFileName);
        File.Move(sourcePath, destination);
        return destination;
    }

    public static bool IsInside(string path, string folder)
    {
        var fullPath = Path.GetFullPath(path);
        var fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveSourcePath(LabelRecord record, string readyFolder)
    {
        if (!string.IsNullOrWhiteSpace(record.SourcePath) && File.Exists(record.SourcePath))
            return record.SourcePath;

        var legacyPath = Path.Combine(readyFolder, record.OriginalFileName);
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "Unknown recipient" : safe[..Math.Min(safe.Length, 60)];
    }
}
