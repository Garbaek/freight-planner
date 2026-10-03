using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FreightCopilot;

public static class ContractIdentity
{
    public static string Fingerprint(ContractScan scan)
    {
        var cargo = scan.Cargo.Select(x => string.Join("|", Normalize(x.Commodity), x.Scu,
                Normalize(x.Pickup), Normalize(x.Destination)))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var raw = $"{Normalize(scan.Title)}::{string.Join("::", cargo)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }

    public static string PlanFingerprint(ManifestResult manifest)
    {
        var contracts = manifest.Contracts.Select(Fingerprint).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var route = manifest.LocalRoute.Select(ManifestOptimizer.NormalizeLocation);
        var raw = $"{string.Join('|', contracts)}::{string.Join('|', route)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..20];
    }

    public static bool SameMission(ContractScan left, ContractScan right)
    {
        if (Fingerprint(left).Equals(Fingerprint(right), StringComparison.OrdinalIgnoreCase)) return true;
        var leftDetails = DetailsSignature(left);
        var rightDetails = DetailsSignature(right);
        return leftDetails.Length >= 80 && leftDetails.Equals(rightDetails, StringComparison.Ordinal);
    }

    public static List<ContractScan> CollapseDuplicates(IEnumerable<ContractScan> scans)
    {
        var unique = new List<ContractScan>();
        foreach (var scan in scans.OrderBy(x => x.CapturedAtUtc))
        {
            var duplicate = unique.FirstOrDefault(x => SameMission(x, scan));
            if (duplicate is null) unique.Add(scan);
            else MergeBest(duplicate, scan);
        }
        return unique;
    }

    public static void MergeBest(ContractScan target, ContractScan incoming)
    {
        var selected = target.SelectedForPlan;
        var manualOverride = target.ManualOverride;
        var replaceCore = !target.ManuallyCorrected &&
                          (incoming.ManuallyCorrected ||
                           incoming.IsEligible && !target.IsEligible ||
                           incoming.Confidence > target.Confidence ||
                           Math.Abs(incoming.Confidence - target.Confidence) < 0.0001 &&
                           (incoming.Warnings.Count < target.Warnings.Count ||
                            incoming.Warnings.Count == target.Warnings.Count && InformationScore(incoming) > InformationScore(target)));
        if (replaceCore)
        {
            target.Title = incoming.Title;
            target.Contractor = incoming.Contractor;
            target.Rank = incoming.Rank;
            target.Reward = incoming.Reward;
            target.MaxContainerScu = incoming.MaxContainerScu;
            target.AvailabilityMinutes = incoming.AvailabilityMinutes;
            target.ContractDeadlineNotApplicable = incoming.ContractDeadlineNotApplicable;
            target.CapturedAtUtc = incoming.CapturedAtUtc;
            target.Cargo = incoming.Cargo;
            target.PickupLocations = incoming.PickupLocations;
            target.DestinationLocations = incoming.DestinationLocations;
            target.IsComplete = incoming.IsComplete;
            target.Notes = incoming.Notes;
            target.Confidence = incoming.Confidence;
            target.Warnings = incoming.Warnings;
            target.RecoveredFields = incoming.RecoveredFields;
            target.ManuallyCorrected = incoming.ManuallyCorrected;
        }
        else if (incoming.CapturedAtUtc > target.CapturedAtUtc && incoming.AvailabilityMinutes is > 0)
        {
            target.AvailabilityMinutes = incoming.AvailabilityMinutes;
            target.CapturedAtUtc = incoming.CapturedAtUtc;
        }
        if (incoming.RawOcrText.Length > target.RawOcrText.Length) target.RawOcrText = incoming.RawOcrText;
        if (File.Exists(incoming.SourceFile)) target.SourceFile = incoming.SourceFile;
        target.SelectedForPlan = selected;
        target.ManualOverride = manualOverride;
    }

    private static int InformationScore(ContractScan scan)
    {
        var score = scan.Reward >= 1_000 ? 2_000 : 0;
        score += scan.AvailabilityMinutes is > 0 || scan.ContractDeadlineNotApplicable ? 300 : 0;
        score += scan.Cargo.Count * 500;
        score += scan.Cargo.Count(x => x.Scu > 0) * 100;
        score += scan.Cargo.Sum(x => (x.Commodity?.Length ?? 0) + (x.Pickup?.Length ?? 0) + (x.Destination?.Length ?? 0));
        score += scan.RawOcrText.Length / 20;
        return score;
    }

    private static string DetailsSignature(ContractScan scan)
    {
        if (string.IsNullOrWhiteSpace(scan.RawOcrText)) return "";
        var match = Regex.Match(scan.RawOcrText,
            @"\[DETAILS\]\s*(?<details>.*?)(?=\r?\n\[OBJECTIVES\]|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? Normalize(match.Groups["details"].Value) : "";
    }

    private static string Normalize(string? value) =>
        Regex.Replace((value ?? "").Replace("&", "and", StringComparison.OrdinalIgnoreCase), @"[^a-z0-9]+", "", RegexOptions.IgnoreCase).ToUpperInvariant();
}
