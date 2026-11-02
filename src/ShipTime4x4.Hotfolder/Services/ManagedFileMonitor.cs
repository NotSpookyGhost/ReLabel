namespace ShipTime4x4.Hotfolder.Services;

internal enum ManagedFileState { Present, Missing, Unavailable }

internal sealed class ManagedFileMonitor(TimeProvider? clock = null,
    Func<string, string, ManagedFileState>? probe = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<string, string, ManagedFileState> _probe = probe ?? Probe;
    private readonly Dictionary<string, DateTimeOffset> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    internal static readonly TimeSpan ConfirmationDelay = TimeSpan.FromSeconds(5);

    public bool ConfirmMissing(string path, string root)
    {
        lock (_gate)
        {
            var state = _probe(path, root);
            if (state != ManagedFileState.Missing)
            {
                _missing.Remove(path);
                return false;
            }
            var now = _clock.GetUtcNow();
            if (!_missing.TryGetValue(path, out var since))
            {
                _missing[path] = now;
                return false;
            }
            return now - since >= ConfirmationDelay;
        }
    }

    public void Forget(string path) { lock (_gate) _missing.Remove(path); }

    internal static ManagedFileState Probe(string path, string root)
    {
        try
        {
            if (!PrintedFileMover.IsInside(path, root)) return ManagedFileState.Unavailable;
            // An unavailable share, denied directory, or disconnected disk is NOT a deletion.
            using (var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator())
                _ = entries.MoveNext();
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                    ? ManagedFileState.Present : ManagedFileState.Unavailable;
            }
            catch (FileNotFoundException) { return ManagedFileState.Missing; }
            catch (DirectoryNotFoundException)
            {
                // Recheck the root after the failure, to avoid a share dropping mid-probe.
                using var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
                _ = entries.MoveNext();
                return ManagedFileState.Missing;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ManagedFileState.Unavailable;
        }
    }
}
