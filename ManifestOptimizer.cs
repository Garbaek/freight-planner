using System.Text.RegularExpressions;

namespace FreightCopilot;

public sealed class ManifestResult
{
    public List<ContractScan> Contracts { get; init; } = [];
    public int TotalScu => Contracts.Sum(x => x.TotalScu);
    public int TotalReward => Contracts.Sum(x => x.Reward);
    public string CommonPickup { get; init; } = "";
    public List<string> PickupStops { get; init; } = [];
    public List<string> Stops { get; init; } = [];
    public List<string> LocalRoute { get; init; } = [];
    public DateTime PlanningUtc { get; init; }
    public int EstimatedTripMinutes { get; init; }
    public int EstimatedFlightMinutes => TravelLegs.Sum(x => x.Minutes);
    public List<TravelLegEstimate> TravelLegs { get; init; } = [];
    public int SafetyMarginMinutes { get; init; } = 10;
    public int TightestRemainingMinutes { get; init; }
    public int HeadroomMinutes => TightestRemainingMinutes - EstimatedTripMinutes - SafetyMarginMinutes;
    public bool IsManualSelection { get; init; }
    public bool TimerBudgetOverridden { get; set; }
    public PlanPreference Preference { get; init; } = PlanPreference.Balanced;
    public int UnknownAvailabilityCount => Contracts.Count(x => x.AvailabilityMinutes is not > 0 && !x.ContractDeadlineNotApplicable);
    public int AcceptedContractCount => Contracts.Count(x => x.ContractDeadlineNotApplicable);
    public int UnknownRewardCount => Contracts.Count(x => x.Reward <= 0);
    public int ManualOverrideCount => Contracts.Count(x => x.ManualOverride);
    public bool HasKnownAvailability => Contracts.Any(x => x.AvailabilityMinutes is > 0);
    public bool HasUnverifiedValues => UnknownAvailabilityCount > 0 || UnknownRewardCount > 0 || ManualOverrideCount > 0;
}

public sealed class RouteGuideStep
{
    public int Number { get; init; }
    public string Location { get; init; } = "";
    public int TravelMinutesFromPrevious { get; init; }
    public bool TravelTimeIsRecorded { get; init; }
    public List<string> Deliveries { get; init; } = [];
    public List<string> Pickups { get; init; } = [];
    public List<string> Packing { get; init; } = [];
}

public sealed record PlanAlternative(PlanPreference Preference, string Name, string Description, ManifestResult Manifest);

public static class ManifestOptimizer
{
    public static ManifestResult Build(IReadOnlyList<ContractScan> scans, int capacity, DateTime? planningUtc = null,
        TravelTimeDatabase? travelTimes = null, PlanPreference preference = PlanPreference.Balanced)
    {
        var now = planningUtc ?? DateTime.UtcNow;
        var usable = scans.Where(x => x.IsEligible && x.TotalScu > 0 && x.TotalScu <= capacity && x.PlanningMinutesRemaining(now) > 0).ToList();
        if (usable.Count == 0) return new ManifestResult { PlanningUtc = now, Preference = preference };

        var pickupFrequency = usable.SelectMany(GetPickupLocations).GroupBy(NormalizeLocation)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var destinationFrequency = usable.SelectMany(x => x.Cargo).Select(x => x.Destination).GroupBy(NormalizeLocation)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        // A bounded beam search keeps the best time-feasible combinations. Unlike a
        // cargo-only knapsack, every state must fit the tightest offer countdown with
        // a safety reserve after route and handling time are estimated.
        var states = new List<TripCandidate> { new([], new ManifestResult { PlanningUtc = now }, 0) };
        foreach (var contract in usable.OrderBy(x => x.PlanningMinutesRemaining(now)).ThenByDescending(x => (double)x.Reward / x.TotalScu))
        {
            var expanded = new List<TripCandidate>(states);
            foreach (var state in states)
            {
                if (state.Contracts.Sum(x => x.TotalScu) + contract.TotalScu > capacity) continue;
                var contracts = new List<ContractScan>(state.Contracts) { contract };
                var manifest = CreateManifest(contracts, now, travelTimes: travelTimes, preference: preference);
                if (manifest.HasKnownAvailability && manifest.HeadroomMinutes < 0) continue;
                var balanced = contracts.Sum(x => ContractValue(x, pickupFrequency, destinationFrequency)) -
                               manifest.EstimatedTripMinutes * 1_250 +
                               contracts.Sum(x => Math.Max(0, 120 - x.PlanningMinutesRemaining(now))) * 150;
                var score = preference switch
                {
                    PlanPreference.HighestReward => manifest.TotalReward * 20d - manifest.EstimatedTripMinutes * 750d,
                    PlanPreference.SafestDeadlines => (manifest.HasKnownAvailability ? manifest.HeadroomMinutes : 240) * 100_000d + manifest.TotalReward,
                    PlanPreference.ShortestFlight => -manifest.EstimatedTripMinutes * 100_000d + manifest.TotalReward,
                    _ => balanced
                };
                expanded.Add(new TripCandidate(contracts, manifest, score));
            }
            states = expanded.GroupBy(x => string.Join("|", x.Contracts.Select(c => c.SourceFile).OrderBy(x => x)), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.MaxBy(candidate => candidate.Score)!)
                .OrderByDescending(x => x.Score).Take(384).ToList();
        }

        return states.Where(x => x.Contracts.Count > 0).MaxBy(x => x.Score)?.Manifest ?? new ManifestResult { PlanningUtc = now, Preference = preference };
    }

    public static List<PlanAlternative> BuildAlternatives(IReadOnlyList<ContractScan> scans, int capacity,
        DateTime? planningUtc = null, TravelTimeDatabase? travelTimes = null)
    {
        var choices = new[]
        {
            new PlanAlternative(PlanPreference.HighestReward, "Highest reward", "Maximizes safe combined aUEC within capacity and deadlines.",
                Build(scans, capacity, planningUtc, travelTimes, PlanPreference.HighestReward)),
            new PlanAlternative(PlanPreference.SafestDeadlines, "Safest deadlines", "Maximizes the remaining timer margin after the estimated run.",
                Build(scans, capacity, planningUtc, travelTimes, PlanPreference.SafestDeadlines)),
            new PlanAlternative(PlanPreference.ShortestFlight, "Shortest flight", "Prefers the quickest useful contract run.",
                Build(scans, capacity, planningUtc, travelTimes, PlanPreference.ShortestFlight))
        };
        return choices.Where(x => x.Manifest.Contracts.Count > 0).ToList();
    }

    public static ManifestResult BuildSelected(IReadOnlyList<ContractScan> selected, int capacity, DateTime? planningUtc = null, TravelTimeDatabase? travelTimes = null)
    {
        var now = planningUtc ?? DateTime.UtcNow;
        var invalid = selected.Any(x => !x.IsEligible && !(x.ManualOverride && x.CanUseManualOverride));
        var expired = selected.Any(x => x.AvailabilityMinutes is > 0 && x.RemainingMinutes(now) <= 0);
        if (selected.Count == 0 || invalid || expired || selected.Sum(x => x.TotalScu) > capacity)
            return new ManifestResult { PlanningUtc = now, IsManualSelection = true };
        return CreateManifest(selected.ToList(), now, manualSelection: true, travelTimes: travelTimes);
    }

    public static ManifestResult RefreshExisting(ManifestResult existing, int capacity, DateTime? planningUtc = null,
        TravelTimeDatabase? travelTimes = null)
    {
        var now = planningUtc ?? DateTime.UtcNow;
        if (existing.Contracts.Count == 0 || existing.Contracts.Sum(x => x.TotalScu) > capacity)
            return new ManifestResult { PlanningUtc = now, Preference = existing.Preference };
        return CreateManifest(existing.Contracts.ToList(), now, existing.IsManualSelection, travelTimes, existing.Preference);
    }

    private static ManifestResult CreateManifest(List<ContractScan> selected, DateTime planningUtc, bool manualSelection = false,
        TravelTimeDatabase? travelTimes = null, PlanPreference preference = PlanPreference.Balanced)
    {
        var displays = BuildLocationDisplays(selected);
        var pickups = selected.SelectMany(GetPickupLocations).Select(x => DisplayFor(x, displays))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var required = selected.SelectMany(x => GetPickupLocations(x).Concat(x.Cargo.Select(c => c.Destination)))
            .Select(x => DisplayFor(x, displays)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var route = BuildLocalRoute(selected, displays, travelTimes);
        var travelLegs = travelTimes?.EstimateRoute(route) ?? BuildFallbackTravelLegs(route);
        var estimate = EstimateTripMinutes(selected, route, travelTimes);
        return new ManifestResult
        {
            Contracts = selected,
            PickupStops = pickups,
            CommonPickup = pickups.Count == 1 ? pickups[0] : $"{pickups.Count} pickup locations",
            Stops = required,
            LocalRoute = route,
            PlanningUtc = planningUtc,
            EstimatedTripMinutes = estimate,
            TravelLegs = travelLegs,
            TightestRemainingMinutes = selected.Where(x => x.AvailabilityMinutes is > 0)
                .Select(x => x.RemainingMinutes(planningUtc)).DefaultIfEmpty(0).Min(),
            IsManualSelection = manualSelection,
            Preference = preference
        };
    }

    public static string NormalizeLocation(string value)
    {
        if (Regex.IsMatch(value, @"\bTeasa\s+Spaceport\b", RegexOptions.IgnoreCase)) return "TEASA SPACEPORT";
        value = value.Replace("&", " and ", StringComparison.OrdinalIgnoreCase);
        value = Regex.Replace(value, @"\b(?:on\s+Hurston|in\s+Lorville)\b", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bfreight\s+elevator\s+at\b", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"[^a-z0-9]+", " ", RegexOptions.IgnoreCase);
        return Regex.Replace(value, @"\s+", " ").Trim().ToUpperInvariant();
    }

    public static bool ValidateRoute(IReadOnlyList<string> route, ManifestResult manifest, out string error)
    {
        error = "";
        if (route.Count == 0) { error = "The route is empty."; return false; }
        if (route.Count > manifest.Stops.Count * 3 + 5) { error = "The route repeats too many stops."; return false; }
        var required = manifest.Stops.Select(NormalizeLocation).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = route.Select(NormalizeLocation).ToList();
        if (actual.Any(x => !required.Contains(x))) { error = "The route introduced an unknown stop."; return false; }
        if (!required.SetEquals(actual)) { error = "The route omitted a required pickup or destination."; return false; }
        if (actual.Zip(actual.Skip(1)).Any(pair => pair.First.Equals(pair.Second, StringComparison.OrdinalIgnoreCase)))
        { error = "The route duplicated a stop consecutively."; return false; }

        foreach (var contract in manifest.Contracts)
        {
            var pickups = GetPickupLocations(contract).Select(NormalizeLocation).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var leg in contract.Cargo)
            {
                var destination = NormalizeLocation(leg.Destination);
                var feasible = actual.Select((location, index) => (location, index))
                    .Where(x => x.location.Equals(destination, StringComparison.OrdinalIgnoreCase))
                    .Any(drop => pickups.All(pickup => actual.Take(drop.index)
                        .Any(x => x.Equals(pickup, StringComparison.OrdinalIgnoreCase))));
                if (!feasible)
                {
                    error = $"The route reaches {leg.Destination} before all required pickups for {contract.Title}.";
                    return false;
                }
            }
        }
        return true;
    }

    public static List<string> BuildPackingOrder(IReadOnlyList<string> route, ManifestResult manifest)
    {
        var events = GetDeliveryEvents(route, manifest.Contracts)
            .GroupBy(x => x.RouteIndex).OrderBy(x => x.Key)
            .Select(x => route[x.Key]).ToList();
        events.Reverse();
        return events.DistinctBy(NormalizeLocation).ToList();
    }

    public static List<RouteGuideStep> BuildRouteGuide(IReadOnlyList<string> route, ManifestResult manifest, TravelTimeDatabase? travelTimes = null)
    {
        if (!ValidateRoute(route, manifest, out _)) route = manifest.LocalRoute;
        var events = GetDeliveryEvents(route, manifest.Contracts);
        var steps = new List<RouteGuideStep>();
        for (var routeIndex = 0; routeIndex < route.Count; routeIndex++)
        {
            var canonical = NormalizeLocation(route[routeIndex]);
            var deliveries = events.Where(x => x.RouteIndex == routeIndex)
                .Select(x => $"Drop off {x.Leg.Scu:N0} SCU {x.Leg.Commodity} ({x.Contract.Reward:N0} aUEC job)").ToList();
            var pickupContracts = manifest.Contracts.Where(contract => GetPickupLocations(contract).Any(x => NormalizeLocation(x) == canonical) &&
                                                                       route.Take(routeIndex).All(x => NormalizeLocation(x) != canonical)).ToList();
            var pickups = pickupContracts.SelectMany(contract => contract.Cargo.Select(leg =>
                $"Pick up {leg.Scu:N0} SCU {leg.Commodity} for {leg.Destination}{ContainerNote(contract, leg.Scu)}")).ToList();
            var loadGroups = events.Where(delivery => GetLoadIndex(delivery.Contract, route, delivery.RouteIndex) == routeIndex)
                .GroupBy(x => x.RouteIndex).OrderByDescending(x => x.Key).ToList();
            var packing = new List<string>();
            for (var groupIndex = 0; groupIndex < loadGroups.Count; groupIndex++)
            {
                var group = loadGroups[groupIndex];
                var zone = loadGroups.Count == 1 && routeIndex > 0 ? "near-left / newly freed zone" :
                    groupIndex == 0 ? "far-right / deepest zone" :
                    groupIndex == loadGroups.Count - 1 ? "far-left / most accessible zone" : "center zone";
                foreach (var cargo in group.GroupBy(x => new { x.Leg.Commodity, x.Contract.MaxContainerScu }))
                {
                    var scu = cargo.Sum(x => x.Leg.Scu);
                    var containers = cargo.Key.MaxContainerScu is > 0 ? $" — crates: {CratePlanner.Describe(scu, cargo.Key.MaxContainerScu)}" : "";
                    packing.Add($"Pack {scu:N0} SCU {cargo.Key.Commodity}{containers} for {MapLocationLabel(route[group.Key])} into the {zone}");
                }
            }
            steps.Add(new RouteGuideStep
            {
                Number = routeIndex + 1,
                Location = MapLocationLabel(route[routeIndex]),
                TravelMinutesFromPrevious = routeIndex == 0 ? 0 :
                    (travelTimes?.Estimate(route[routeIndex - 1], route[routeIndex]).Minutes ??
                     manifest.TravelLegs.ElementAtOrDefault(routeIndex - 1)?.Minutes ?? 6),
                TravelTimeIsRecorded = routeIndex > 0 &&
                    (travelTimes?.Estimate(route[routeIndex - 1], route[routeIndex]).IsRecorded ??
                     manifest.TravelLegs.ElementAtOrDefault(routeIndex - 1)?.IsRecorded ?? false),
                Deliveries = deliveries,
                Pickups = pickups,
                Packing = packing
            });
        }
        return steps;
    }

    public static List<string> BuildContractChecklist(ManifestResult manifest)
    {
        return manifest.Contracts.OrderByDescending(x => x.Reward).ThenBy(x => x.Title)
            .Select((contract, index) =>
            {
                var pickups = string.Join("; ", GetPickupLocations(contract).Select(MapLocationLabel).Distinct(StringComparer.OrdinalIgnoreCase));
                var destinations = string.Join("; ", contract.Cargo.Select(x => MapLocationLabel(x.Destination)).Distinct(StringComparer.OrdinalIgnoreCase));
                var reward = contract.Reward > 0 ? $"{contract.Reward:N0} aUEC" : "REWARD UNREAD";
                var timer = contract.AvailabilityMinutes is > 0
                    ? $"{FormatDuration(contract.RemainingMinutes(manifest.PlanningUtc))} left"
                    : contract.ContractDeadlineNotApplicable ? "ACCEPTED — no offer timer" : "TIMER UNREAD";
                var warning = contract.ManualOverride ? "⚠ OVERRIDE — " : "";
                return $"{warning}Contract #{index + 1} — {reward} — {contract.Title} — {contract.TotalScu:N0} SCU — {pickups} → {destinations} — {timer}";
            }).ToList();
    }

    public static string MapLocationLabel(string location) =>
        NormalizeLocation(location).Equals("TEASA SPACEPORT", StringComparison.OrdinalIgnoreCase)
            ? "Teasa Spaceport — Lorville, Hurston"
            : location.Trim().TrimEnd('.', ',');

    public static string Format(ManifestResult result, ShipProfile ship, IReadOnlyList<ContractScan> batch,
        string? planWarning = null)
    {
        var lines = new List<string>();
        if (result.Contracts.Count == 0)
        {
            lines.Add($"NO AUTOMATIC ACCEPTANCE — {ship.Name}");
            lines.Add("No locally validated trip fits capacity, route time, countdown, and the 10-minute safety reserve.");
        }
        else
        {
            var route = result.LocalRoute;
            route = route.Select(MapLocationLabel).ToList();
            lines.Add(result.TimerBudgetOverridden
                ? $"⚠ CHECKED CONTRACT PLAN — TIMER OVERRIDE ACTIVE — {ship.Name}"
                : result.HasUnverifiedValues
                ? $"⚠ CHECKED CONTRACT PLAN WITH MANUAL OVERRIDES — {ship.Name}"
                : result.HeadroomMinutes >= 0
                ? $"✓ {(result.IsManualSelection ? "CHECKED CONTRACT PLAN" : "VALIDATED RECOMMENDATION")} — {ship.Name}"
                : $"⚠ CHECKED CONTRACT PLAN EXCEEDS TIMER BUDGET — {ship.Name}");
            lines.Add($"Pickups: {string.Join("; ", result.PickupStops)}");
            lines.Add($"Cargo: {result.TotalScu:N0} / {ship.CapacityScu:N0} SCU (capacity enforced locally)");
            lines.Add(result.UnknownRewardCount > 0
                ? $"Known reward: {result.TotalReward:N0} aUEC + {result.UnknownRewardCount} unread reward(s)"
                : $"Combined reward: {result.TotalReward:N0} aUEC (calculated locally)");
            lines.Add($"Estimated run: ~{FormatDuration(result.EstimatedTripMinutes)} + {result.SafetyMarginMinutes}m safety reserve");
            if (!result.IsManualSelection) lines.Add($"Plan goal: {PreferenceLabel(result.Preference)}");
            if (result.TravelLegs.Count > 0)
            {
                var recorded = result.TravelLegs.Count(x => x.IsRecorded);
                lines.Add($"Estimated flight: ~{FormatDuration(result.EstimatedFlightMinutes)} across {result.TravelLegs.Count} leg(s) — {recorded} recorded, {result.TravelLegs.Count - recorded} default estimate(s)");
            }
            if (result.UnknownAvailabilityCount > 0)
            {
                var knownTimer = result.HasKnownAvailability
                    ? $" Tightest readable timer: {FormatDuration(result.TightestRemainingMinutes)} — {FormatDuration(result.HeadroomMinutes)} headroom."
                    : " No selected timer could be verified.";
                lines.Add($"⚠ Availability unverified for {result.UnknownAvailabilityCount} contract(s).{knownTimer}");
            }
            else if (result.HasKnownAvailability)
                lines.Add($"Tightest availability: {FormatDuration(result.TightestRemainingMinutes)} remaining — {FormatDuration(result.HeadroomMinutes)} headroom");
            else lines.Add("Offer countdown: not applicable — selected contracts are already accepted");
            lines.Add($"Route: {string.Join(" → ", route)}");
            lines.Add("");
            lines.Add(result.TimerBudgetOverridden
                ? "TIMER BUDGET OVERRIDE ACTIVE — DEADLINES MAY EXPIRE BEFORE COMPLETION"
                : result.HasUnverifiedValues
                ? "MANUAL OVERRIDE — VERIFY UNREAD VALUES IN GAME BEFORE ACCEPTING"
                : result.HeadroomMinutes >= 0
                ? $"ACCEPT THESE CONTRACTS IN ORDER (1 → {result.Contracts.Count})"
                : "NOT TIME-SAFE AS SELECTED — UNCHECK ONE OR MORE CONTRACTS");
            if (result.HasUnverifiedValues || result.TimerBudgetOverridden) lines.Add($"ACCEPT THESE CONTRACTS IN ORDER (1 → {result.Contracts.Count})");
            foreach (var contractLine in BuildContractChecklist(result)) lines.Add($"• {contractLine}");

            AppendTravelTimeBreakdown(lines, result);
            AppendRouteActions(lines, route, result);
            AppendLoadPlan(lines, route, result);
        }

        if (!string.IsNullOrWhiteSpace(planWarning))
        {
            lines.Add("");
            lines.Add("⚠ PLAN NOTE");
            lines.Add(planWarning.Trim());
        }

        var deferred = batch.Where(x => !result.Contracts.Contains(x) && x.IsEligible && x.PlanningMinutesRemaining(result.PlanningUtc) > 0).ToList();
        if (deferred.Count > 0)
        {
            lines.Add("");
            lines.Add(result.IsManualSelection ? "VALID BUT UNCHECKED" : "VALID BUT DEFERRED — CONSIDER FOR THE NEXT RUN");
            foreach (var scan in deferred.OrderBy(x => x.PlanningMinutesRemaining(result.PlanningUtc)).ThenByDescending(x => x.Reward))
            {
                var timer = scan.ContractDeadlineNotApplicable ? "accepted — no offer timer" : $"{FormatDuration(scan.RemainingMinutes(result.PlanningUtc))} left";
                lines.Add($"• {scan.Reward:N0} aUEC — {scan.TotalScu:N0} SCU — {string.Join("; ", GetPickupLocations(scan).Select(MapLocationLabel))} → {string.Join("; ", scan.Cargo.Select(x => MapLocationLabel(x.Destination)).Distinct(StringComparer.OrdinalIgnoreCase))} — {timer}");
            }
            lines.Add(result.IsManualSelection
                ? "Check different jobs and press Plan checked jobs to rebuild the route."
                : "Deferred contracts are valid, but adding them would reduce the deadline safety margin or route efficiency.");
        }

        var excluded = batch.Where(x => !result.Contracts.Contains(x) &&
            (!x.IsEligible || (x.AvailabilityMinutes is > 0 && x.RemainingMinutes(result.PlanningUtc) <= 0))).ToList();
        if (excluded.Count > 0)
        {
            lines.Add("");
            lines.Add("WARNINGS — NOT AUTOMATICALLY ACCEPTED");
            foreach (var scan in excluded)
            {
                var reason = scan.IsEligible && scan.AvailabilityMinutes is > 0 && scan.RemainingMinutes(result.PlanningUtc) <= 0 ? "Contract availability expired after the screenshot was captured." :
                    !scan.IsEligible ? scan.Notes : scan.TotalScu > ship.CapacityScu
                    ? $"{scan.TotalScu:N0} SCU exceeds {ship.CapacityScu:N0} SCU capacity"
                    : "Not selected.";
                lines.Add($"⚠ {Path.GetFileName(scan.SourceFile)}: {reason}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatDuration(int minutes)
    {
        minutes = Math.Max(0, minutes);
        return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:D2}m" : $"{minutes}m";
    }

    private static void AppendTravelTimeBreakdown(List<string> lines, ManifestResult result)
    {
        if (result.TravelLegs.Count == 0) return;
        lines.Add("");
        lines.Add("TRAVEL TIME BREAKDOWN");
        foreach (var leg in result.TravelLegs)
            lines.Add($"• {MapLocationLabel(leg.From)} → {MapLocationLabel(leg.To)} — ~{leg.Minutes}m ({leg.Source})");
    }

    private static void AppendRouteActions(List<string> lines, IReadOnlyList<string> route, ManifestResult result)
    {
        var deliveryEvents = GetDeliveryEvents(route, result.Contracts);
        lines.Add("");
        lines.Add("ROUTE ACTIONS (pickup-before-delivery validated)");
        for (var index = 0; index < route.Count; index++)
        {
            var canonical = NormalizeLocation(route[index]);
            var pickups = result.Contracts.Where(contract => GetPickupLocations(contract).Any(x => NormalizeLocation(x) == canonical) &&
                                                             route.Take(index).All(x => NormalizeLocation(x) != canonical)).ToList();
            var deliveries = deliveryEvents.Where(x => x.RouteIndex == index).ToList();
            var actionParts = new List<string>();
            var pickupScu = pickups.Where(x => GetPickupLocations(x).Count == 1).Sum(x => x.TotalScu);
            if (pickups.Count > 0) actionParts.Add(pickupScu > 0 ? $"PICK UP {pickupScu:N0} SCU" : "COMPLETE PICKUP OBJECTIVES");
            if (deliveries.Count > 0) actionParts.Add($"DELIVER {deliveries.Sum(x => x.Leg.Scu):N0} SCU");
            if (actionParts.Count == 0) actionParts.Add("TRANSIT / REPOSITION");
            lines.Add($"{index + 1}. {MapLocationLabel(route[index])} — {string.Join(" + ", actionParts)}");
            foreach (var contract in pickups)
            {
                foreach (var leg in contract.Cargo)
                    lines.Add($"   ↑ {leg.Scu:N0} SCU {leg.Commodity} → {MapLocationLabel(leg.Destination)}{ContainerNote(contract, leg.Scu)}");
            }
            foreach (var delivery in deliveries)
                lines.Add($"   ↓ {delivery.Leg.Scu:N0} SCU {delivery.Leg.Commodity} — {delivery.Contract.Reward:N0} aUEC contract");
        }
    }

    private static void AppendLoadPlan(List<string> lines, IReadOnlyList<string> route, ManifestResult result)
    {
        var events = GetDeliveryEvents(route, result.Contracts);
        lines.Add("");
        lines.Add("DETERMINISTIC LOAD / STACK PLAN BY PICKUP");
        lines.Add("At combined stops, unload due cargo first. Then load later deliveries deepest and the next delivery most accessible.");
        for (var routeIndex = 0; routeIndex < route.Count; routeIndex++)
        {
            var loads = events.Where(delivery => GetLoadIndex(delivery.Contract, route, delivery.RouteIndex) == routeIndex)
                .GroupBy(x => x.RouteIndex).OrderByDescending(x => x.Key).ToList();
            if (loads.Count == 0) continue;
            var total = loads.Sum(group => group.Sum(x => x.Leg.Scu));
            lines.Add($"{MapLocationLabel(route[routeIndex])} — load {total:N0} SCU after completing any delivery here:");
            for (var zoneIndex = 0; zoneIndex < loads.Count; zoneIndex++)
            {
                var group = loads[zoneIndex];
                var placement = loads.Count == 1 ? "use the newly freed/clearest zone" :
                    zoneIndex == 0 ? "load first / deepest" : zoneIndex == loads.Count - 1 ? "load last / most accessible" : "middle zone";
                lines.Add($"   {zoneIndex + 1}. For {MapLocationLabel(route[group.Key])} — {group.Sum(x => x.Leg.Scu):N0} SCU — {placement}");
                foreach (var cargo in group.GroupBy(x => new { x.Leg.Commodity, x.Contract.MaxContainerScu }))
                {
                    var max = cargo.Key.MaxContainerScu is > 0 ? $", crates: {CratePlanner.Describe(cargo.Sum(x => x.Leg.Scu), cargo.Key.MaxContainerScu)}" : "";
                    lines.Add($"      • {cargo.Sum(x => x.Leg.Scu):N0} SCU {cargo.Key.Commodity}{max}");
                }
            }
        }
    }

    private static int GetLoadIndex(ContractScan contract, IReadOnlyList<string> route, int beforeDeliveryIndex)
    {
        var routeLocations = route.Select(NormalizeLocation).ToList();
        var pickupIndices = GetPickupLocations(contract).Select(NormalizeLocation).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(pickup => Enumerable.Range(0, beforeDeliveryIndex)
                .FirstOrDefault(index => routeLocations[index].Equals(pickup, StringComparison.OrdinalIgnoreCase), -1))
            .ToList();
        return pickupIndices.Count > 0 && pickupIndices.All(index => index >= 0) ? pickupIndices.Max() : -1;
    }

    private static List<DeliveryEvent> GetDeliveryEvents(IReadOnlyList<string> route, IReadOnlyList<ContractScan> contracts)
    {
        var actual = route.Select(NormalizeLocation).ToList();
        var events = new List<DeliveryEvent>();
        foreach (var contract in contracts)
        {
            var pickups = GetPickupLocations(contract).Select(NormalizeLocation).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var leg in contract.Cargo)
            {
                var destination = NormalizeLocation(leg.Destination);
                var index = Enumerable.Range(0, actual.Count).FirstOrDefault(i =>
                    actual[i].Equals(destination, StringComparison.OrdinalIgnoreCase) &&
                    pickups.All(pickup => actual.Take(i).Any(x => x.Equals(pickup, StringComparison.OrdinalIgnoreCase))), -1);
                if (index >= 0) events.Add(new DeliveryEvent(index, contract, leg));
            }
        }
        return events;
    }

    private static List<string> BuildLocalRoute(IReadOnlyList<ContractScan> contracts, IReadOnlyDictionary<string, string> displays,
        TravelTimeDatabase? travelTimes)
    {
        var picked = new HashSet<(ContractScan Contract, string Pickup)>();
        var delivered = new HashSet<CargoLeg>();
        var route = new List<string>();
        var pickupLocations = contracts.SelectMany(contract => GetPickupLocations(contract).Select(location => (contract, location))).ToList();
        var current = pickupLocations.GroupBy(x => NormalizeLocation(x.location), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(x => x.contract.TotalScu) + group.Sum(x => x.contract.Reward) / 1000.0)
            .First().Key;

        for (var guard = 0; guard < 200 && delivered.Count < contracts.Sum(x => x.Cargo.Count); guard++)
        {
            if (route.Count == 0 || NormalizeLocation(route[^1]) != current) route.Add(displays[current]);
            foreach (var item in pickupLocations.Where(x => NormalizeLocation(x.location) == current))
                picked.Add((item.contract, current));
            foreach (var contract in contracts)
            {
                var requiredPickups = GetPickupLocations(contract).Select(NormalizeLocation).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (!requiredPickups.All(x => picked.Contains((contract, x)))) continue;
                foreach (var leg in contract.Cargo.Where(x => NormalizeLocation(x.Destination) == current)) delivered.Add(leg);
            }
            if (delivered.Count == contracts.Sum(x => x.Cargo.Count)) break;

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var contract in contracts)
            {
                var requiredPickups = GetPickupLocations(contract).Select(NormalizeLocation).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var pickup in requiredPickups.Where(x => !picked.Contains((contract, x)))) candidates.Add(pickup);
                if (requiredPickups.All(x => picked.Contains((contract, x))))
                    foreach (var leg in contract.Cargo.Where(x => !delivered.Contains(x))) candidates.Add(NormalizeLocation(leg.Destination));
            }
            if (candidates.Count == 0) break;
            current = candidates.OrderByDescending(candidate => CandidateScore(candidate, current, contracts, picked, delivered, route, travelTimes)).First();
        }
        return route;
    }

    private static double CandidateScore(string candidate, string current, IReadOnlyList<ContractScan> contracts,
        HashSet<(ContractScan Contract, string Pickup)> picked, HashSet<CargoLeg> delivered, IReadOnlyList<string> route,
        TravelTimeDatabase? travelTimes)
    {
        double score = 0;
        foreach (var contract in contracts)
        {
            var pickups = GetPickupLocations(contract).Select(NormalizeLocation).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (pickups.Contains(candidate) && !picked.Contains((contract, candidate))) score += 40_000 + contract.TotalScu * 300;
            if (pickups.All(x => picked.Contains((contract, x))))
                score += contract.Cargo.Where(x => !delivered.Contains(x) && NormalizeLocation(x.Destination) == candidate)
                    .Sum(x => 100_000 + x.Scu * 500);
        }
        if (route.Any(x => NormalizeLocation(x) == candidate)) score -= 5_000;
        var fromDisplay = route.Count > 0 ? route[^1] : current;
        var toDisplay = candidate;
        var travelMinutes = travelTimes?.Estimate(fromDisplay, toDisplay).Minutes ?? 6;
        score -= travelMinutes * 2_500;
        return score;
    }

    private static double ContractValue(ContractScan contract, IReadOnlyDictionary<string, int> pickupFrequency,
        IReadOnlyDictionary<string, int> destinationFrequency)
    {
        var pickupBonus = GetPickupLocations(contract).Select(NormalizeLocation).Distinct(StringComparer.OrdinalIgnoreCase)
            .Sum(x => Math.Max(0, pickupFrequency.GetValueOrDefault(x) - 1) * 3_000);
        var destinationBonus = contract.Cargo.Select(x => NormalizeLocation(x.Destination)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Sum(x => Math.Max(0, destinationFrequency.GetValueOrDefault(x) - 1) * 2_000);
        return contract.Reward + pickupBonus + destinationBonus + contract.TotalScu * 25;
    }

    private static int EstimateTripMinutes(IReadOnlyList<ContractScan> contracts, IReadOnlyList<string> route, TravelTimeDatabase? travelTimes)
    {
        var travelMinutes = travelTimes?.EstimateRoute(route).Sum(x => x.Minutes) ?? Math.Max(1, route.Count - 1) * 6;
        var containers = contracts.Sum(contract => contract.Cargo.Sum(leg =>
            (int)Math.Ceiling((double)leg.Scu / Math.Max(1, contract.MaxContainerScu ?? 4))));
        var cargoHandlingMinutes = (int)Math.Ceiling(containers * 0.75);
        var fallbackCargoPerStop = route.Count == 0 ? 0 : (int)Math.Ceiling((double)cargoHandlingMinutes / route.Count);
        var stopHandlingMinutes = route.Sum(location =>
        {
            var handling = travelTimes?.EstimateHandling(location);
            return handling is { IsRecorded: true }
                ? handling.Minutes
                : (travelTimes?.Settings.HandlingMinutesPerStop ?? 2) + fallbackCargoPerStop;
        });
        return (travelTimes?.Settings.FixedPreflightMinutes ?? 5) + travelMinutes + stopHandlingMinutes;
    }

    private static List<TravelLegEstimate> BuildFallbackTravelLegs(IReadOnlyList<string> route)
    {
        var legs = new List<TravelLegEstimate>();
        for (var i = 1; i < route.Count; i++) legs.Add(new TravelLegEstimate(route[i - 1], route[i], 6, false));
        return legs;
    }

    private static List<string> GetPickupLocations(ContractScan contract) => contract.PickupLocations.Count > 0
        ? contract.PickupLocations
        : contract.Cargo.SelectMany(x => x.Pickup.Split(" + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static Dictionary<string, string> BuildLocationDisplays(IEnumerable<ContractScan> contracts)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in contracts.SelectMany(x => GetPickupLocations(x).Concat(x.Cargo.Select(c => c.Destination))))
        {
            var canonical = NormalizeLocation(location);
            var display = MapLocationLabel(location);
            if (!result.TryGetValue(canonical, out var existing) || display.Length > existing.Length) result[canonical] = display;
        }
        return result;
    }

    private static string DisplayFor(string location, IReadOnlyDictionary<string, string> displays) =>
        displays.GetValueOrDefault(NormalizeLocation(location), location.Trim().TrimEnd('.', ','));
    private static string ContainerNote(ContractScan contract, int scu) => CrateNote(scu, contract.MaxContainerScu);

    public static string CrateNote(int scu, int? maximumContainerScu) => maximumContainerScu is > 0
        ? $" (crates: {CratePlanner.Describe(scu, maximumContainerScu)})" : "";

    public static int EstimateSingleContractMinutes(ContractScan contract, TravelTimeDatabase? travelTimes = null) =>
        CreateManifest([contract], DateTime.UtcNow, travelTimes: travelTimes).EstimatedTripMinutes;

    public static string PreferenceLabel(PlanPreference preference) => preference switch
    {
        PlanPreference.HighestReward => "Highest reward",
        PlanPreference.SafestDeadlines => "Safest deadlines",
        PlanPreference.ShortestFlight => "Shortest flight",
        _ => "Balanced"
    };

    private sealed record TripCandidate(List<ContractScan> Contracts, ManifestResult Manifest, double Score);
    private sealed record DeliveryEvent(int RouteIndex, ContractScan Contract, CargoLeg Leg);
}
