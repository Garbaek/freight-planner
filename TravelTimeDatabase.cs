using System.Text.Json;

namespace FreightCopilot;

public sealed class TravelTimeSettings
{
    public int SchemaVersion { get; set; } = 1;
    public int DefaultSameBodyMinutes { get; set; } = 6;
    public int DefaultCrossBodyMinutes { get; set; } = 14;
    public int DefaultUnknownMinutes { get; set; } = 8;
    public int FixedPreflightMinutes { get; set; } = 5;
    public int HandlingMinutesPerStop { get; set; } = 2;
    public List<TravelTimeLeg> Legs { get; set; } = [];
    public List<HandlingTimeProfile> HandlingProfiles { get; set; } = [];
}

public sealed class HandlingTimeProfile
{
    public string Location { get; set; } = "";
    public string ShipName { get; set; } = "";
    public int Minutes { get; set; } = 2;
    public int SampleCount { get; set; }
    public int TotalSeconds { get; set; }
}

public sealed record HandlingTimeEstimate(int Minutes, bool IsRecorded, int SampleCount = 0);

public sealed class TravelTimeLeg
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Minutes { get; set; } = 6;
    public bool Bidirectional { get; set; } = true;
    public string ShipName { get; set; } = "";
    public int SampleCount { get; set; }
    public int TotalMinutes { get; set; }
}

public sealed record TravelLegEstimate(string From, string To, int Minutes, bool IsRecorded, int SampleCount = 0, string ShipName = "")
{
    public string Source => SampleCount > 0 ? $"learned average for {ShipName} ({SampleCount} run{(SampleCount == 1 ? "" : "s")})" :
        IsRecorded ? "recorded" : "default estimate";
}

public sealed class TravelTimeDatabase
{
    private readonly string _path;
    public TravelTimeSettings Settings { get; private set; }
    public string ActiveShipName { get; set; } = "";
    public string PathOnDisk => _path;
    public long Revision
    {
        get
        {
            try { return File.Exists(_path) ? File.GetLastWriteTimeUtc(_path).Ticks : 0; }
            catch { return 0; }
        }
    }

    public TravelTimeDatabase(string appDirectory)
    {
        Directory.CreateDirectory(appDirectory);
        _path = Path.Combine(appDirectory, "travel-times.json");
        Settings = Load();
        if (!File.Exists(_path)) Save();
    }

    public TravelLegEstimate Estimate(string from, string to)
    {
        var fromKey = ManifestOptimizer.NormalizeLocation(from);
        var toKey = ManifestOptimizer.NormalizeLocation(to);
        if (fromKey.Equals(toKey, StringComparison.OrdinalIgnoreCase))
            return new TravelLegEstimate(from, to, 0, true);

        var matches = Settings.Legs.Where(leg =>
        {
            var direct = ManifestOptimizer.NormalizeLocation(leg.From).Equals(fromKey, StringComparison.OrdinalIgnoreCase) &&
                         ManifestOptimizer.NormalizeLocation(leg.To).Equals(toKey, StringComparison.OrdinalIgnoreCase);
            var reverse = leg.Bidirectional && ManifestOptimizer.NormalizeLocation(leg.From).Equals(toKey, StringComparison.OrdinalIgnoreCase) &&
                          ManifestOptimizer.NormalizeLocation(leg.To).Equals(fromKey, StringComparison.OrdinalIgnoreCase);
            return direct || reverse;
        }).ToList();
        var selected = matches.Where(x => x.SampleCount > 0 && x.ShipName.Equals(ActiveShipName, StringComparison.OrdinalIgnoreCase))
                           .OrderByDescending(x => x.SampleCount).FirstOrDefault()
                       ?? matches.Where(x => x.SampleCount == 0 && string.IsNullOrWhiteSpace(x.ShipName)).LastOrDefault()
                       ?? matches.OrderByDescending(x => x.SampleCount).FirstOrDefault();
        if (selected is not null)
            return new TravelLegEstimate(from, to, Math.Max(1, selected.Minutes), true, selected.SampleCount, selected.ShipName);

        var fromBody = BodyFor(from);
        var toBody = BodyFor(to);
        var minutes = fromBody.Length > 0 && fromBody.Equals(toBody, StringComparison.OrdinalIgnoreCase)
            ? Settings.DefaultSameBodyMinutes
            : fromBody.Length > 0 && toBody.Length > 0
                ? Settings.DefaultCrossBodyMinutes
                : Settings.DefaultUnknownMinutes;
        return new TravelLegEstimate(from, to, Math.Max(1, minutes), false);
    }

    public List<TravelLegEstimate> EstimateRoute(IReadOnlyList<string> route)
    {
        var result = new List<TravelLegEstimate>();
        for (var i = 1; i < route.Count; i++) result.Add(Estimate(route[i - 1], route[i]));
        return result;
    }

    public void ReplaceLegs(IEnumerable<TravelTimeLeg> legs)
    {
        Settings.Legs = legs.Where(x => !string.IsNullOrWhiteSpace(x.From) && !string.IsNullOrWhiteSpace(x.To) && x.Minutes > 0)
            .GroupBy(x => PairKey(x.From, x.To, x.Bidirectional, x.ShipName), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last()).OrderBy(x => x.From).ThenBy(x => x.To).ToList();
        Save();
    }

    public TravelTimeLeg RecordObservation(string from, string to, int minutes, string shipName)
    {
        minutes = Math.Max(1, minutes);
        shipName = string.IsNullOrWhiteSpace(shipName) ? "Unknown ship" : shipName.Trim();
        var key = PairKey(from, to, true, shipName);
        var leg = Settings.Legs.FirstOrDefault(x => PairKey(x.From, x.To, x.Bidirectional, x.ShipName)
            .Equals(key, StringComparison.OrdinalIgnoreCase));
        if (leg is null)
        {
            leg = new TravelTimeLeg { From = from, To = to, Bidirectional = true, ShipName = shipName };
            Settings.Legs.Add(leg);
        }
        if (leg.SampleCount > 0 && leg.TotalMinutes <= 0) leg.TotalMinutes = leg.Minutes * leg.SampleCount;
        leg.SampleCount++;
        leg.TotalMinutes += minutes;
        leg.Minutes = Math.Max(1, (int)Math.Round((double)leg.TotalMinutes / leg.SampleCount));
        Save();
        return leg;
    }

    public HandlingTimeEstimate EstimateHandling(string location)
    {
        var locationKey = ManifestOptimizer.NormalizeLocation(location);
        var exact = Settings.HandlingProfiles
            .Where(x => x.SampleCount > 0 && x.ShipName.Equals(ActiveShipName, StringComparison.OrdinalIgnoreCase) &&
                        ManifestOptimizer.NormalizeLocation(x.Location).Equals(locationKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.SampleCount).FirstOrDefault();
        if (exact is not null) return new HandlingTimeEstimate(Math.Max(1, exact.Minutes), true, exact.SampleCount);

        var shipSamples = Settings.HandlingProfiles
            .Where(x => x.SampleCount > 0 && x.ShipName.Equals(ActiveShipName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (shipSamples.Count > 0)
        {
            var seconds = shipSamples.Sum(x => x.TotalSeconds);
            var samples = shipSamples.Sum(x => x.SampleCount);
            return new HandlingTimeEstimate(Math.Max(1, (int)Math.Ceiling(seconds / 60d / samples)), true, samples);
        }
        return new HandlingTimeEstimate(Math.Max(1, Settings.HandlingMinutesPerStop), false);
    }

    public HandlingTimeProfile RecordHandlingObservation(string location, TimeSpan elapsed, string shipName)
    {
        shipName = string.IsNullOrWhiteSpace(shipName) ? "Unknown ship" : shipName.Trim();
        var seconds = Math.Clamp((int)Math.Round(elapsed.TotalSeconds), 15, 4 * 60 * 60);
        var locationKey = ManifestOptimizer.NormalizeLocation(location);
        var profile = Settings.HandlingProfiles.FirstOrDefault(x =>
            x.ShipName.Equals(shipName, StringComparison.OrdinalIgnoreCase) &&
            ManifestOptimizer.NormalizeLocation(x.Location).Equals(locationKey, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            profile = new HandlingTimeProfile { Location = location, ShipName = shipName };
            Settings.HandlingProfiles.Add(profile);
        }
        if (profile.SampleCount > 0 && profile.TotalSeconds <= 0) profile.TotalSeconds = profile.Minutes * 60 * profile.SampleCount;
        profile.SampleCount++;
        profile.TotalSeconds += seconds;
        profile.Minutes = Math.Max(1, (int)Math.Ceiling(profile.TotalSeconds / 60d / profile.SampleCount));
        Save();
        return profile;
    }

    public void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(Settings, JsonDefaults.Options()));
    }

    private TravelTimeSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<TravelTimeSettings>(File.ReadAllText(_path), JsonDefaults.Options()) ?? new();
        }
        catch { }
        return new TravelTimeSettings();
    }

    private static string PairKey(string from, string to, bool bidirectional, string shipName)
    {
        var a = ManifestOptimizer.NormalizeLocation(from);
        var b = ManifestOptimizer.NormalizeLocation(to);
        if (bidirectional && string.Compare(a, b, StringComparison.OrdinalIgnoreCase) > 0) (a, b) = (b, a);
        return $"{a}|{b}|{bidirectional}|{shipName.Trim().ToUpperInvariant()}";
    }

    private static string BodyFor(string location)
    {
        var value = location.ToUpperInvariant();
        if (value.Contains("HURSTON") || value.Contains("TEASA SPACEPORT") || value.StartsWith("HDMS-") || value.StartsWith("HDPC-")) return "Hurston";
        if (value.Contains("MICROTECH") || value.Contains("NEW BABBAGE") || value.Contains("PORT TRESSLER")) return "microTech";
        if (value.Contains("ARCCORP") || value.Contains("AREA18") || value.Contains("BAIJINI")) return "ArcCorp";
        if (value.Contains("CRUSADER") || value.Contains("ORISON") || value.Contains("PORT OLISAR")) return "Crusader";
        return "";
    }
}
