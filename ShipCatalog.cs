using System.Text.Json;

namespace FreightCopilot;

public static class ShipCatalog
{
    private static readonly ShipProfile[] Fallback =
    [
        new("ARGO RAFT", 192),
        new("Crusader C1 Spirit", 64),
        new("Drake Cutlass Black", 46),
        new("MISC Freelancer MAX", 120),
        new("RSI Hermes", 288)
    ];

    public static IReadOnlyList<ShipProfile> Load(string baseDirectory)
    {
        var profiles = LoadFile(Path.Combine(baseDirectory, "ships.json"));
        if (profiles.Count == 0) profiles = Fallback.ToList();

        return profiles
            .Where(x => !string.Equals(x.Name, "Custom", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Append(new ShipProfile("Custom", 192))
            .ToArray();
    }

    private static List<ShipProfile> LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var document = JsonSerializer.Deserialize<ShipCatalogDocument>(File.ReadAllText(path), JsonDefaults.Options());
            return document?.Ships
                .Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.CapacityScu is > 0 and <= 5000)
                .Select(x => new ShipProfile(x.Name.Trim(), x.CapacityScu))
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }
}

public sealed class ShipCatalogDocument
{
    public int SchemaVersion { get; set; }
    public string VerifiedForPatch { get; set; } = "";
    public DateTime VerifiedAtUtc { get; set; }
    public List<string> Sources { get; set; } = [];
    public List<ShipCatalogEntry> Ships { get; set; } = [];
}

public sealed class ShipCatalogEntry
{
    public string Name { get; set; } = "";
    public int CapacityScu { get; set; }
    public string Note { get; set; } = "";
}
