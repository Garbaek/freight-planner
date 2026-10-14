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

    // More cells than any ship has; a hand-edited grid beyond it is left out rather than searched.
    private const int MaxGridCells = 20_000;

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

    // The ship of that name, or the one that carried the name before the game renamed it.
    public static ShipProfile? Find(IReadOnlyList<ShipProfile> ships, string name) =>
        ships.FirstOrDefault(x => x.Name == name) ??
        ships.FirstOrDefault(x => x.FormerNames?.Contains(name, StringComparer.OrdinalIgnoreCase) == true);

    // The game patch the grids in ships.json were read from; empty when the file does not say.
    public static string VerifiedForPatch(string baseDirectory) => Read(Path.Combine(baseDirectory, "ships.json"))?.VerifiedForPatch ?? "";

    // What the ship holds, grid by grid, and how many crates of each size that comes to.
    public static string DescribeCargo(ShipProfile ship, string verifiedForPatch = "")
    {
        var grids = ship.Grids ?? [];
        var lines = new List<string>();
        if (grids.Count == 0)
        {
            lines.Add($"{ship.Name} — {ship.CapacityScu:N0} SCU");
            lines.Add("");
            lines.Add("No cargo grid is listed for this ship in ships.json, so only its SCU capacity is used. Whether a load of crates physically fits is not checked.");
        }
        else
        {
            var cells = grids.Sum(x => x.Scu);
            lines.Add($"{ship.Name} — {cells:N0} SCU on {grids.Count} cargo grid{(grids.Count == 1 ? "" : "s")}");
            lines.Add("");
            foreach (var kind in grids.GroupBy(x => x))
                lines.Add($"   {(kind.Count() > 1 ? $"{kind.Count()} × " : "")}{kind.Key.Width} wide × {kind.Key.Height} high × {kind.Key.Length} long = {kind.Key.Scu:N0} SCU" +
                          (kind.Count() > 1 ? " each" : "") + (kind.Key.MaxCrateScu > 0 ? $", crates up to {kind.Key.MaxCrateScu} SCU" : ""));
            lines.Add("");
            lines.Add("Loaded with crates of one size only, it takes:");
            var none = new List<int>();
            var unsure = false;
            foreach (var size in CratePlanner.CrateSizes)
            {
                var (count, certain) = CratePlanner.MostOf(size, grids);
                unsure |= !certain;
                if (count == 0) none.Add(size);
                else lines.Add($"   {(certain ? "" : "at least ")}{count:N0} × {size} SCU = {count * size:N0} SCU");
            }
            if (none.Count > 0) lines.Add($"   No {string.Join(", ", none)} SCU crate fits.");
            if (unsure) lines.Add("   \"At least\": room was found for that many, and more could not be ruled out.");
            if (ship.CapacityScu != cells)
            {
                lines.Add("");
                lines.Add($"The capacity is set to {ship.CapacityScu:N0} SCU; the grids hold {cells:N0} SCU.");
            }
        }
        if (ship.Note.Length > 0)
        {
            lines.Add("");
            lines.Add(ship.Note);
        }
        if (grids.Count > 0)
        {
            lines.Add("");
            lines.Add("A crate snaps to whole grid cells and stands on the deck or on other crates." +
                      (verifiedForPatch.Length > 0 ? $" The grids are from the game files of patch {verifiedForPatch}." : ""));
        }
        return string.Join(Environment.NewLine, lines);
    }

    // The grids in a few words, for next to the capacity.
    public static string SummarizeCargo(ShipProfile ship)
    {
        var grids = ship.Grids ?? [];
        if (grids.Count == 0) return "no cargo grid listed";
        var largest = grids.Max(CratePlanner.LargestCrate);
        var shape = grids.Count == 1 ? $"grid {grids[0].Width} × {grids[0].Height} × {grids[0].Length}" : $"{grids.Count} grids";
        return $"{shape}, crates up to {largest} SCU";
    }

    private static List<ShipProfile> LoadFile(string path) =>
        Read(path)?.Ships
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.CapacityScu is > 0 and <= 5000)
            .Select(x => new ShipProfile(x.Name.Trim(), x.CapacityScu, Grids(x), (x.Note ?? "").Trim(), x.FormerNames ?? []))
            .ToList() ?? [];

    private static List<CargoGrid> Grids(ShipCatalogEntry entry)
    {
        var grids = (entry.Grids ?? []).Where(g => g is { Width: > 0, Height: > 0, Length: > 0 })
            .SelectMany(g => Enumerable.Repeat(new CargoGrid(g.Width, g.Height, g.Length, Math.Max(0, g.MaxCrateScu)), Math.Clamp(g.Count, 1, 64)))
            .ToList();
        return grids.Sum(x => (long)x.Scu) <= MaxGridCells ? grids : [];
    }

    private static ShipCatalogDocument? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ShipCatalogDocument>(File.ReadAllText(path), JsonDefaults.Options()) : null;
        }
        catch
        {
            return null;
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
    public List<CargoGridEntry> Grids { get; set; } = [];
    public List<string> FormerNames { get; set; } = [];
    public string Note { get; set; } = "";
}

// A grid as ships.json lists it; Count stands for that many grids of the same size.
public sealed class CargoGridEntry
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Length { get; set; }
    public int Count { get; set; } = 1;
    public int MaxCrateScu { get; set; }
}
