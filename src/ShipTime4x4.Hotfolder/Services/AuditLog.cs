using System.Text.Json;

namespace ShipTime4x4.Hotfolder.Services;

public sealed class AuditLog
{
    private readonly object _gate = new();
    private readonly string _folder;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public AuditLog(string dataFolder)
    {
        _folder = Path.Combine(dataFolder, "Logs");
        Directory.CreateDirectory(_folder);
    }

    public string CurrentPath => Path.Combine(_folder, $"audit-{DateTime.UtcNow:yyyy-MM}.jsonl");

    public void Write(string action, Guid? labelId, string? fileName, string outcome, string? detail = null)
    {
        var entry = new
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Action = action,
            LabelId = labelId,
            FileName = fileName,
            Outcome = outcome,
            Detail = detail
        };
        var line = JsonSerializer.Serialize(entry, Options) + Environment.NewLine;
        lock (_gate)
            File.AppendAllText(CurrentPath, line);
    }
}
