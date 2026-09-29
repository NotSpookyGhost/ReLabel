namespace ShipTime4x4.Hotfolder.Services;

public static class ManagedFileNames
{
    public static string NextAvailable(string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        var safeName = Path.GetFileName(fileName);
        var candidate = Path.Combine(folder, safeName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var number = 1; number < int.MaxValue; number++)
        {
            candidate = Path.Combine(folder, $"{stem} ({number}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException($"Could not create a unique name for {safeName}.");
    }
}
