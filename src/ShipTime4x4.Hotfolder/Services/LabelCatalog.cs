using System.Text.Json;
using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.Services;

public sealed class LabelCatalog
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<LabelRecord> _records;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public LabelCatalog(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "labels.json");
        _records = Load();
    }

    public IReadOnlyList<LabelRecord> GetAll()
    {
        lock (_gate)
            return _records.OrderByDescending(x => x.ImportedAt).ToArray();
    }

    public LabelRecord? Find(Guid id)
    {
        lock (_gate)
            return _records.FirstOrDefault(x => x.Id == id);
    }

    public bool ContainsFingerprint(string fingerprint)
    {
        lock (_gate)
            return _records.Any(x => string.Equals(x.SourceFingerprint, fingerprint, StringComparison.Ordinal));
    }

    public void Add(LabelRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            SaveLocked();
        }
    }

    public void Update(LabelRecord record)
    {
        lock (_gate)
        {
            var index = _records.FindIndex(x => x.Id == record.Id);
            if (index < 0)
                throw new InvalidOperationException("The label is no longer in the catalog.");
            _records[index] = record;
            SaveLocked();
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var removed = _records.RemoveAll(x => x.Id == id) > 0;
            if (removed)
                SaveLocked();
            return removed;
        }
    }

    private List<LabelRecord> Load()
    {
        if (!File.Exists(_path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<LabelRecord>>(File.ReadAllText(_path), Options) ?? [];
        }
        catch (JsonException)
        {
            File.Copy(_path, _path + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            return [];
        }
    }

    private void SaveLocked()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_records, Options));
        File.Move(temporary, _path, true);
    }
}
