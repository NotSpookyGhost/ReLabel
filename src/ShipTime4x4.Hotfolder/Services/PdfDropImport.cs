namespace ShipTime4x4.Hotfolder.Services;

internal static class PdfDropImport
{
    public static bool IsPdfPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public static Task<string> CopyToIncomingAsync(string source, string incomingFolder) => Task.Run(() =>
    {
        if (!IsPdfPath(source))
            throw new InvalidDataException("Only PDF files can be added.");
        var fullSource = Path.GetFullPath(source);
        var incoming = Path.TrimEndingDirectorySeparator(Path.GetFullPath(incomingFolder));
        if (!File.Exists(fullSource))
            throw new FileNotFoundException("The dropped PDF is not an accessible file.", fullSource);

        // Files already in Incoming belong to the normal watch-folder workflow.
        if (string.Equals(Path.GetDirectoryName(fullSource), incoming, StringComparison.OrdinalIgnoreCase))
            return fullSource;

        Directory.CreateDirectory(incoming);
        var stagingPath = Path.Combine(incoming, $".relabel-drop-{Guid.NewGuid():N}.tmp");
        using var input = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read);
        var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            // Do not expose a .pdf to the watcher until its complete copy is ready.
            using (output) input.CopyTo(output);
            while (true)
            {
                var destination = ManagedFileNames.NextAvailable(incoming, Path.GetFileName(fullSource));
                try
                {
                    File.Move(stagingPath, destination, overwrite: false);
                    return destination;
                }
                catch (IOException) when (File.Exists(destination))
                {
                    // Another drop or network user claimed the filename. Never overwrite it.
                }
            }
        }
        finally
        {
            output.Dispose();
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
        }
    });
}
