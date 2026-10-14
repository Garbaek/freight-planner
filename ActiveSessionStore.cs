using System.Text.Json;

namespace FreightCopilot;

public sealed class ActiveSessionSnapshot
{
    public int SchemaVersion { get; set; } = 2;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<ContractScan> Contracts { get; set; } = [];
    public ExecutionProgress? Execution { get; set; }
    public TradeRunState? ActiveTradeRun { get; set; }
}

public sealed class ActiveSessionStore
{
    private readonly string _path;
    public string PathOnDisk => _path;

    public ActiveSessionStore(string appDirectory) => _path = Path.Combine(appDirectory, "active-session.json");

    public ActiveSessionSnapshot Load()
    {
        var candidates = new[] { _path, _path + ".tmp" }
            .Where(File.Exists)
            .Select(path =>
            {
                try { return JsonSerializer.Deserialize<ActiveSessionSnapshot>(File.ReadAllText(path), JsonDefaults.Options()); }
                catch { return null; }
            })
            .OfType<ActiveSessionSnapshot>()
            .ToList();
        if (candidates.Count > 0) return candidates.MaxBy(x => x.UpdatedAtUtc)!;
        // Zero distinguishes a missing/corrupt legacy session from a valid schema-2 session with no active run.
        return new ActiveSessionSnapshot { SchemaVersion = 0 };
    }

    public void Save(IReadOnlyList<ContractScan> contracts, ExecutionProgress? execution, TradeRun? activeTradeRun = null)
    {
        var snapshot = new ActiveSessionSnapshot
        {
            UpdatedAtUtc = DateTime.UtcNow,
            Contracts = contracts.ToList(),
            Execution = execution,
            ActiveTradeRun = activeTradeRun is { IsCompleted: false } ? activeTradeRun.ToState() : null
        };
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonDefaults.Options()));
        File.Move(temporary, _path, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
            if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
        }
        catch { }
    }
}
