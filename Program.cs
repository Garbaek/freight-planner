namespace FreightCopilot;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length >= 3 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
        {
            RunSelfTestAsync(args[1], args[2]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 3 && args[0].Equals("--batch-self-test", StringComparison.OrdinalIgnoreCase))
        {
            RunBatchSelfTestAsync(args[1], args[2]).GetAwaiter().GetResult();
            return;
        }
        Application.Run(new MainForm());
    }

    private static async Task RunSelfTestAsync(string imagePath, string reportPath)
    {
        try
        {
            var ocr = new LocalOcrService();
            var text = await ocr.ReadTextAsync(imagePath, CancellationToken.None);
            var scan = MissionParser.Parse(text, imagePath);
            var report = new
            {
                engine = ocr.EngineDescription,
                scan.Title,
                scan.Reward,
                scan.TotalScu,
                scan.MaxContainerScu,
                scan.AvailabilityMinutes,
                scan.ContractDeadlineNotApplicable,
                scan.Pickups,
                scan.Destinations,
                scan.IsComplete,
                scan.IsEligible,
                scan.Confidence,
                scan.Warnings,
                scan.RecoveredFields,
                scan.Cargo,
                raw_ocr_text = text
            };
            await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(report, JsonDefaults.Options()));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(new { error = ex.ToString() }, JsonDefaults.Options()));
        }
    }

    private static async Task RunBatchSelfTestAsync(string screenshotFolder, string reportPath)
    {
        try
        {
            var ocr = new LocalOcrService();
            var scans = new List<ContractScan>();
            var files = Directory.EnumerateFiles(screenshotFolder)
                .Where(path => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(File.GetLastWriteTimeUtc).TakeLast(20);
            foreach (var file in files)
                scans.Add(MissionParser.Parse(await ocr.ReadTextAsync(file, CancellationToken.None), file));
            MissionParser.ReconcileBatch(scans);
            // Historical screenshots are evaluated at the newest capture time so the
            // diagnostic tests the countdown algorithm rather than today's wall clock.
            var simulatedPlanningUtc = scans.Count > 0 ? scans.Max(x => x.CapturedAtUtc) : DateTime.UtcNow;
            var travelTimes = new TravelTimeDatabase(AppContext.BaseDirectory);
            var manifest = ManifestOptimizer.Build(scans, 192, simulatedPlanningUtc, travelTimes);
            if (manifest.TotalScu > 192 || manifest.Contracts.Any(scan => !scan.IsEligible))
                throw new InvalidOperationException("Deterministic manifest validation failed.");
            var report = new
            {
                engine = ocr.EngineDescription,
                screenshots = scans.Count,
                eligible_records = scans.Count(x => x.IsEligible),
                excluded_records = scans.Count(x => !x.IsEligible),
                simulated_planning_utc = simulatedPlanningUtc,
                manifest_scu = manifest.TotalScu,
                manifest_reward = manifest.TotalReward,
                estimated_trip_minutes = manifest.EstimatedTripMinutes,
                tightest_remaining_minutes = manifest.TightestRemainingMinutes,
                timer_headroom_minutes = manifest.HeadroomMinutes,
                manifest_pickup = manifest.CommonPickup,
                manifest_locations = manifest.Stops,
                deterministic_route = manifest.LocalRoute,
                route_guide = ManifestOptimizer.BuildRouteGuide(manifest.LocalRoute, manifest, travelTimes),
                travel_time_file = travelTimes.PathOnDisk,
                recorded_travel_legs = manifest.TravelLegs.Count(x => x.IsRecorded),
                default_travel_legs = manifest.TravelLegs.Count(x => !x.IsRecorded),
                formatted_manifest = ManifestOptimizer.Format(manifest, new ShipProfile("ARGO RAFT", 192), scans),
                records = scans.Select(x => new
                {
                    source = Path.GetFileName(x.SourceFile), x.Title, x.Reward, x.TotalScu, x.AvailabilityMinutes,
                    x.CapturedAtUtc, remaining_minutes = x.RemainingMinutes(simulatedPlanningUtc), x.IsEligible, x.Warnings
                }),
                accepted = manifest.Contracts.Select(x => new { source = Path.GetFileName(x.SourceFile), x.Title, x.Reward, x.TotalScu, x.Pickups, x.Destinations }),
                excluded = scans.Where(x => !x.IsEligible).Select(x => new { source = Path.GetFileName(x.SourceFile), x.Title, x.Reward, x.TotalScu, x.Warnings })
            };
            await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(report, JsonDefaults.Options()));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(new { error = ex.ToString() }, JsonDefaults.Options()));
        }
    }
}
