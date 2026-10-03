using System.Text.Json;

namespace FreightCopilot;

public sealed class ActiveSessionSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<ContractScan> Contracts { get; set; } = [];
    public ExecutionProgress? Execution { get; set; }
}

public sealed class ActiveSessionStore
{
    private readonly string _path;
    public string PathOnDisk => _path;

    public ActiveSessionStore(string appDirectory) => _path = Path.Combine(appDirectory, "active-session.json");

    public ActiveSessionSnapshot Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<ActiveSessionSnapshot>(File.ReadAllText(_path), JsonDefaults.Options()) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public void Save(IReadOnlyList<ContractScan> contracts, ExecutionProgress? execution)
    {
        var snapshot = new ActiveSessionSnapshot
        {
            UpdatedAtUtc = DateTime.UtcNow,
            Contracts = contracts.ToList(),
            Execution = execution
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, JsonDefaults.Options()));
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }
}
