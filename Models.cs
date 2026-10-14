using System.Text.Json.Serialization;

namespace FreightCopilot;

public sealed class CargoLeg
{
    [JsonPropertyName("commodity")] public string Commodity { get; set; } = "Unknown";
    [JsonPropertyName("scu")] public int Scu { get; set; }
    [JsonPropertyName("pickup")] public string Pickup { get; set; } = "Unknown";
    [JsonPropertyName("destination")] public string Destination { get; set; } = "Unknown";
}

public sealed class ContractScan
{
    [JsonPropertyName("title")] public string Title { get; set; } = "Unknown contract";
    [JsonPropertyName("contractor")] public string Contractor { get; set; } = "Unknown";
    [JsonPropertyName("rank")] public string Rank { get; set; } = "Unknown";
    [JsonPropertyName("reward_auec")] public int Reward { get; set; }
    [JsonPropertyName("max_container_scu")] public int? MaxContainerScu { get; set; }
    [JsonPropertyName("availability_minutes_at_capture")] public int? AvailabilityMinutes { get; set; }
    [JsonPropertyName("contract_deadline_not_applicable")] public bool ContractDeadlineNotApplicable { get; set; }
    [JsonPropertyName("captured_at_utc")] public DateTime CapturedAtUtc { get; set; }
    [JsonPropertyName("cargo")] public List<CargoLeg> Cargo { get; set; } = [];
    [JsonPropertyName("pickup_locations")] public List<string> PickupLocations { get; set; } = [];
    [JsonPropertyName("destination_locations")] public List<string> DestinationLocations { get; set; } = [];
    [JsonPropertyName("is_complete")] public bool IsComplete { get; set; }
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = [];
    [JsonPropertyName("recovered_fields")] public List<string> RecoveredFields { get; set; } = [];
    [JsonPropertyName("raw_ocr_text")] public string RawOcrText { get; set; } = "";
    [JsonPropertyName("source_file")] public string SourceFile { get; set; } = "";
    [JsonPropertyName("selected_for_plan")] public bool SelectedForPlan { get; set; }
    [JsonPropertyName("manual_override")] public bool ManualOverride { get; set; }
    [JsonPropertyName("manually_corrected")] public bool ManuallyCorrected { get; set; }
    [JsonIgnore] public int TotalScu => Cargo.Sum(x => x.Scu);
    [JsonIgnore] public string Pickups => string.Join(", ", (PickupLocations.Count > 0 ? PickupLocations : Cargo.Select(x => x.Pickup))
        .Distinct(StringComparer.OrdinalIgnoreCase));
    [JsonIgnore] public string Destinations => string.Join(", ", (DestinationLocations.Count > 0 ? DestinationLocations : Cargo.Select(x => x.Destination))
        .Distinct(StringComparer.OrdinalIgnoreCase));
    [JsonIgnore] public bool IsSuspicious => Warnings.Count > 0;
    [JsonIgnore] public bool HasPlannableRouteData => TotalScu > 0 && Cargo.Count > 0 &&
        Cargo.All(x => x.Scu > 0 && !IsUnknown(x.Pickup) && !IsUnknown(x.Destination));
    [JsonIgnore] public bool CanUseManualOverride => HasPlannableRouteData;
    [JsonIgnore] public bool IsEligible => IsComplete && !IsSuspicious && Reward >= 1_000 && TotalScu > 0 &&
                                           (AvailabilityMinutes is > 0 || ContractDeadlineNotApplicable) &&
                                           Cargo.Count > 0 && Cargo.All(x => x.Scu > 0 &&
                                               !IsUnknown(x.Pickup) && !IsUnknown(x.Destination));
    [JsonIgnore] public string Quality => ManualOverride && SelectedForPlan ? "Override" : ManuallyCorrected && IsEligible ? "Corrected" : IsEligible
        ? ContractDeadlineNotApplicable || RemainingMinutes(DateTime.UtcNow) > 0 ? "Validated" : "Expired"
        : IsSuspicious ? "Warning" : "Incomplete";
    [JsonIgnore] public string TimeLeft
    {
        get
        {
            if (ContractDeadlineNotApplicable) return "Accepted";
            var minutes = RemainingMinutes(DateTime.UtcNow);
            if (minutes <= 0) return AvailabilityMinutes is > 0 ? "Expired" : "Unknown";
            return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:D2}m" : $"{minutes}m";
        }
    }

    private static bool IsUnknown(string value) => string.IsNullOrWhiteSpace(value) || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    public int RemainingMinutes(DateTime planningUtc)
    {
        if (AvailabilityMinutes is not > 0) return 0;
        var captured = CapturedAtUtc == default ? planningUtc : CapturedAtUtc;
        var elapsed = Math.Max(0, (planningUtc - captured).TotalMinutes);
        // The game only shows whole minutes. Do not consume a full displayed minute
        // merely because a screenshot is a few seconds old.
        return Math.Max(0, AvailabilityMinutes.Value - (int)Math.Floor(elapsed));
    }

    public int PlanningMinutesRemaining(DateTime planningUtc) =>
        ContractDeadlineNotApplicable ? int.MaxValue : RemainingMinutes(planningUtc);
}

// One rectangular cargo grid in 1-SCU cells. Ships without grids are limited by total SCU only.
// MaxCrateScu is set where the grid takes only smaller crates than its shape has room for; 0 when the shape decides.
public sealed record CargoGrid(int Width, int Height, int Length, int MaxCrateScu = 0)
{
    public int Scu => Width * Height * Length;
}

// FormerNames are what the ship was called in earlier catalogs, before the game renamed it.
public sealed record ShipProfile(string Name, int CapacityScu, IReadOnlyList<CargoGrid>? Grids = null, string Note = "",
    IReadOnlyList<string>? FormerNames = null);

public enum PlanPreference
{
    Balanced,
    HighestReward,
    SafestDeadlines,
    ShortestFlight
}

public sealed class ExecutionProgress
{
    public string PlanFingerprint { get; set; } = "";
    public string ShipName { get; set; } = "";
    public List<string> Route { get; set; } = [];
    public int CurrentStopIndex { get; set; }
    public bool CurrentStopCompleted { get; set; }
    public DateTime? LegStartedAtUtc { get; set; }
    public string TimingFrom { get; set; } = "";
    public string TimingTo { get; set; } = "";
    public Dictionary<string, bool> CompletedTasks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> LoadedScu { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> TabletChecklist { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, DateTime> StopStartedAtUtc { get; set; } = [];
    public Dictionary<int, DateTime> StopCompletedAtUtc { get; set; } = [];
    public HashSet<int> RecordedHandlingStops { get; set; } = [];
    public HashSet<int> RecordedTravelArrivals { get; set; } = [];
}

public sealed class AppSettings
{
    // Empty means "locate the game's screenshot folder on first start".
    public string ScreenshotFolder { get; set; } = "";
    public string ShipName { get; set; } = "ARGO RAFT";
    public int ShipCapacity { get; set; } = 192;
    public PlanPreference PlanPreference { get; set; } = PlanPreference.Balanced;
    public string ApiBaseUrl { get; set; } = "https://freightplanner.cipherdispatch.com";
    // The station last picked in Trade ideas.
    public string TradeStation { get; set; } = "";
    public TradeSettings Trade { get; set; } = new();
}
