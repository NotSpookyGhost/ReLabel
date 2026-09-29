using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ShipTime4x4.Core.Processing;
using ShipTime4x4.Core.Raster;

namespace ShipTime4x4.Hotfolder.Services;

public sealed class PreparedLabelDiskCache
{
    private const int FormatVersion = 2;
    private readonly string _folder;
    private readonly object _gate = new();

    public PreparedLabelDiskCache(string dataFolder)
    {
        _folder = Path.Combine(dataFolder, "PreparedCache");
        Directory.CreateDirectory(_folder);
    }

    public bool TryGet(Guid labelId, string key, out PreparedLabel prepared)
    {
        var path = PathFor(labelId, key);
        prepared = null!;
        if (!File.Exists(path)) return false;
        try
        {
            lock (_gate)
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var compressed = new DeflateStream(file, CompressionMode.Decompress);
                using var reader = new BinaryReader(compressed, Encoding.UTF8, leaveOpen: false);
                if (reader.ReadInt32() != FormatVersion) return false;
                var width = reader.ReadInt32();
                var height = reader.ReadInt32();
                var pixelLength = reader.ReadInt32();
                if (width <= 0 || height <= 0 || pixelLength != checked(width * height)) return false;
                var pixels = reader.ReadBytes(pixelLength);
                if (pixels.Length != pixelLength) return false;
                var zplLength = reader.ReadInt32();
                if (zplLength <= 0 || zplLength > 200_000_000) return false;
                var zpl = reader.ReadBytes(zplLength);
                if (zpl.Length != zplLength) return false;
                var fallback = reader.ReadBoolean();
                var warning = reader.ReadBoolean() ? reader.ReadString() : null;
                var compensation = reader.ReadInt32();
                var pageCount = reader.ReadInt32();
                if (pageCount <= 0) return false;
                prepared = new PreparedLabel(new GrayImage(width, height, pixels), zpl, fallback, warning,
                    compensation, pageCount);
            }
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                           UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            TryDelete(path);
            return false;
        }
    }

    public void Put(Guid labelId, string key, PreparedLabel prepared)
    {
        var path = PathFor(labelId, key);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            lock (_gate)
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var compressed = new DeflateStream(file, CompressionLevel.Fastest))
                using (var writer = new BinaryWriter(compressed, Encoding.UTF8, leaveOpen: false))
                {
                    writer.Write(FormatVersion);
                    writer.Write(prepared.Image.Width);
                    writer.Write(prepared.Image.Height);
                    writer.Write(prepared.Image.Pixels.Length);
                    writer.Write(prepared.Image.Pixels);
                    writer.Write(prepared.Zpl.Length);
                    writer.Write(prepared.Zpl);
                    writer.Write(prepared.UsedFallback);
                    writer.Write(prepared.WarningCode is not null);
                    if (prepared.WarningCode is not null) writer.Write(prepared.WarningCode);
                    writer.Write(prepared.AppliedBarcodeCompensationDots);
                    writer.Write(prepared.PageCount);
                }
                File.Move(temporary, path, true);
            }
            Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
        }
    }

    public void Remove(Guid labelId)
    {
        lock (_gate)
            foreach (var path in Directory.EnumerateFiles(_folder, labelId.ToString("N") + "-*.rlcache"))
                TryDelete(path);
    }

    public void Trim(int maximumEntries = 64, int maximumAgeDays = 14)
    {
        try
        {
            lock (_gate)
            {
                var cutoff = DateTime.UtcNow.AddDays(-maximumAgeDays);
                var files = new DirectoryInfo(_folder).EnumerateFiles("*.rlcache")
                    .OrderByDescending(file => file.LastAccessTimeUtc).ToArray();
                foreach (var file in files.Where((file, index) => index >= maximumEntries || file.LastAccessTimeUtc < cutoff))
                    TryDelete(file.FullName);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string PathFor(Guid labelId, string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_folder, $"{labelId:N}-{hash}.rlcache");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
