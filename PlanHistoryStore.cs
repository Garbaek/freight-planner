using System.Text.Json;

namespace FreightCopilot;

public static class PlanHistoryStore
{
    public static string Save(string appDirectory, ManifestResult manifest, ShipProfile ship,
        IReadOnlyList<ContractScan> batch, string formattedManifest)
    {
        var directory = Path.Combine(appDirectory, "plan-history");
        Directory.CreateDirectory(directory);
        var route = manifest.LocalRoute;
        var snapshot = new
        {
            schema_version = 1,
            created_local = DateTimeOffset.Now,
            created_utc = DateTimeOffset.UtcNow,
            selection_mode = manifest.IsManualSelection ? "checked_contracts" : "automatic_next_run",
            plan_preference = manifest.Preference.ToString(),
            ship = new { name = ship.Name, capacity_scu = ship.CapacityScu },
            totals = new
            {
                cargo_scu = manifest.TotalScu,
                reward_auec = manifest.TotalReward,
                estimated_trip_minutes = manifest.EstimatedTripMinutes,
                estimated_flight_minutes = manifest.EstimatedFlightMinutes,
                safety_margin_minutes = manifest.SafetyMarginMinutes,
                tightest_remaining_minutes = manifest.TightestRemainingMinutes,
                headroom_minutes = manifest.HasKnownAvailability ? manifest.HeadroomMinutes : (int?)null,
                unread_reward_count = manifest.UnknownRewardCount,
                unread_availability_count = manifest.UnknownAvailabilityCount,
                manual_override_count = manifest.ManualOverrideCount
            },
            route,
            travel_legs = manifest.TravelLegs,
            route_guide = ManifestOptimizer.BuildRouteGuide(route, manifest),
            contracts = manifest.Contracts.Select(contract => new
            {
                source_image = contract.SourceFile,
                fingerprint = ContractIdentity.Fingerprint(contract),
                contract.Title,
                reward_auec = contract.Reward > 0 ? contract.Reward : (int?)null,
                manual_override = contract.ManualOverride,
                manually_corrected = contract.ManuallyCorrected,
                contract.RecoveredFields,
                ignored_ocr_warnings = contract.ManualOverride ? contract.Warnings : [],
                total_scu = contract.TotalScu,
                availability_minutes_at_capture = contract.AvailabilityMinutes,
                contract_deadline_not_applicable = contract.ContractDeadlineNotApplicable,
                estimated_remaining_minutes = contract.AvailabilityMinutes is > 0 ? contract.RemainingMinutes(manifest.PlanningUtc) : (int?)null,
                contract.MaxContainerScu,
                pickups = contract.PickupLocations,
                destinations = contract.DestinationLocations,
                cargo = contract.Cargo
            }),
            deferred_sources = batch.Where(x => x.IsEligible && !manifest.Contracts.Contains(x)).Select(x => x.SourceFile),
            formatted_manifest = formattedManifest
        };
        var mode = manifest.IsManualSelection ? "checked" : "auto";
        var file = Path.Combine(directory, $"route-plan-{DateTime.Now:yyyyMMdd-HHmmss}-{mode}-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(snapshot, JsonDefaults.Options()));
        return file;
    }
}
