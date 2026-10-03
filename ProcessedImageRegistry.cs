using System.Text.Json;

namespace FreightCopilot;

public sealed class ProcessedImageRegistry
{
    private readonly string _path;
    private readonly Dictionary<string, ProcessedImageRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public ProcessedImageRegistry(string appDirectory)
    {
        _path = Path.Combine(appDirectory, "ignored-images.json");
        try
        {
            if (!File.Exists(_path)) return;
            var records = JsonSerializer.Deserialize<List<ProcessedImageRecord>>(File.ReadAllText(_path), JsonDefaults.Options()) ?? [];
            foreach (var record in records) _records[record.Path] = record;
        }
        catch { }
    }

    public int Count => _records.Count;

    public bool IsIgnored(string path)
    {
        if (!_records.TryGetValue(path, out var record)) return false;
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == record.Length && info.LastWriteTimeUtc.Ticks == record.LastWriteUtcTicks;
        }
        catch { return false; }
    }

    public void MarkProcessed(string path, bool saveImmediately = true)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            _records[path] = new ProcessedImageRecord(path, info.Length, info.LastWriteTimeUtc.Ticks, DateTime.UtcNow);
            if (saveImmediately) Save();
        }
        catch { }
    }

    public void Flush() => Save();

    public void Clear()
    {
        _records.Clear();
        Save();
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_records.Values.OrderBy(x => x.ProcessedAtUtc), JsonDefaults.Options())); }
        catch { }
    }

    public sealed record ProcessedImageRecord(string Path, long Length, long LastWriteUtcTicks, DateTime ProcessedAtUtc);
}
