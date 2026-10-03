using System.Globalization;
using System.Text.RegularExpressions;

namespace FreightCopilot;

public static partial class MissionParser
{
    public static ContractScan Parse(string rawText, string sourceFile)
    {
        var text = NormalizeText(rawText);
        var header = GetSection(text, "HEADER");
        var details = GetSection(text, "DETAILS");
        var objectives = GetSection(text, "OBJECTIVES");
        var listEdge = GetSection(text, "LIST_EDGE");
        var joinedHeader = Regex.Replace(header, @"\s+", " ").Trim();
        var joinedDetails = Regex.Replace(details, @"\s+", " ").Trim();
        var joinedObjectives = Regex.Replace(objectives, @"\s+", " ").Trim();
        joinedObjectives = Regex.Replace(joinedObjectives, @"\s+[Oo0]◇?\s+(?=(?:Collect|Deliver)\b)", " ", RegexOptions.IgnoreCase);
        // On the contract UI, Windows OCR can collapse the compact "0/7" counter
        // into the letters "on". Keep this correction deliberately narrow.
        joinedObjectives = Regex.Replace(joinedObjectives, @"\bDeliver\s+on\s+SCU\s+of\b", "Deliver 0/7 SCU of", RegexOptions.IgnoreCase);
        DateTime capturedAt;
        try { capturedAt = File.GetLastWriteTimeUtc(sourceFile); }
        catch { capturedAt = DateTime.UtcNow; }
        var scan = new ContractScan { SourceFile = sourceFile, RawOcrText = text, CapturedAtUtc = capturedAt };

        scan.Title = FindTitle(joinedHeader);
        scan.Contractor = FindAfterLabel(joinedHeader, "Contracted By") ?? "Unknown";
        scan.Rank = RankRegex().Match(scan.Title) is { Success: true } rank ? Clean(rank.Groups[1].Value) : "Unknown";
        scan.Reward = FindReward(header);
        scan.ContractDeadlineNotApplicable = Regex.IsMatch(joinedHeader,
            @"Contract\s+Deadline\s+(?:N\s*/?\s*A|Not\s+Applicable)", RegexOptions.IgnoreCase);
        scan.AvailabilityMinutes = FindAvailabilityMinutes(joinedHeader, listEdge, scan.Reward);
        scan.MaxContainerScu = FindMaxContainer(joinedDetails);

        var pickups = ParsePickups(joinedObjectives);
        scan.PickupLocations = pickups.Select(x => x.Location).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (Match match in DeliverRegex().Matches(joinedObjectives))
        {
            var commodity = CleanClause(match.Groups["commodity"].Value);
            var destination = CleanLocation(match.Groups["destination"].Value);
            var scu = ParseNumber(match.Groups["scu"].Value);
            var matchingPickups = pickups.Where(x => CommodityMatches(x.Commodity, commodity)).Select(x => x.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var pickup = matchingPickups.Count > 0 ? string.Join(" + ", matchingPickups) :
                         pickups.Count == 1 ? pickups[0].Location : "Unknown";
            scan.Cargo.Add(new CargoLeg { Commodity = commodity, Scu = scu, Pickup = pickup, Destination = destination });
        }

        // Some OCR passes lose the word "of" while retaining the rest of the objective.
        if (scan.Cargo.Count == 0)
        {
            foreach (Match match in LooseDeliverRegex().Matches(joinedObjectives))
            {
                var commodity = CleanClause(match.Groups["commodity"].Value);
                var pickup = pickups.Count == 1 ? pickups[0].Location : "Unknown";
                scan.Cargo.Add(new CargoLeg
                {
                    Commodity = commodity,
                    Scu = ParseNumber(match.Groups["scu"].Value),
                    Pickup = pickup,
                    Destination = CleanLocation(match.Groups["destination"].Value)
                });
            }
        }
        if (scan.Cargo.Count == 0)
        {
            foreach (Match match in MissingVerbDeliverRegex().Matches(joinedObjectives))
            {
                var commodity = CleanClause(match.Groups["commodity"].Value);
                var matchingPickups = pickups.Where(x => CommodityMatches(x.Commodity, commodity)).Select(x => x.Location)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                scan.Cargo.Add(new CargoLeg
                {
                    Commodity = commodity,
                    Scu = ParseNumber(match.Groups["scu"].Value),
                    Pickup = matchingPickups.Count > 0 ? string.Join(" + ", matchingPickups) : "Unknown",
                    Destination = CleanLocation(match.Groups["destination"].Value)
                });
            }
        }
        if (scan.Cargo.Count == 0)
        {
            // Retain a visibly present objective even when OCR loses both "Deliver"
            // and the denominator. The zero quantity keeps it out of auto-acceptance.
            foreach (Match match in MissingQuantityDeliverRegex().Matches(joinedObjectives))
            {
                scan.Cargo.Add(new CargoLeg
                {
                    Commodity = CleanClause(match.Groups["commodity"].Value),
                    Scu = 0,
                    Pickup = pickups.Count > 0 ? string.Join(" + ", pickups.Select(x => x.Location).Distinct(StringComparer.OrdinalIgnoreCase)) : "Unknown",
                    Destination = CleanLocation(match.Groups["destination"].Value)
                });
            }
        }
        scan.DestinationLocations = scan.Cargo.Select(x => x.Destination).Where(x => !IsUnknown(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Validate(scan, pickups.Count);
        return scan;
    }

    public static void ReconcileBatch(IReadOnlyList<ContractScan> scans)
    {
        // A player often captures the same contract more than once. If one image
        // loses a header value, reuse it only when the complete cargo manifest and
        // route are identical and every readable copy agrees on the value.
        foreach (var group in scans.GroupBy(BuildCargoSignature, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
        {
            var rewards = group.Where(x => x.Reward >= 1_000).Select(x => x.Reward).Distinct().ToList();
            var titles = group.Select(x => x.Title).Where(x => !IsUnknown(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var scan in group)
            {
                var changed = false;
                if (scan.Reward <= 0 && rewards.Count == 1)
                {
                    scan.Reward = rewards[0];
                    scan.RecoveredFields.Add("Reward recovered from an identical contract screenshot.");
                    changed = true;
                }
                if (IsUnknown(scan.Title) && titles.Count == 1)
                {
                    scan.Title = titles[0];
                    scan.Rank = RankRegex().Match(scan.Title) is { Success: true } rank ? Clean(rank.Groups[1].Value) : "Unknown";
                    scan.RecoveredFields.Add("Title recovered from an identical contract screenshot.");
                    changed = true;
                }
                if (changed) Validate(scan, scan.PickupLocations.Count);
            }
        }
    }

    public static void Revalidate(ContractScan scan)
    {
        scan.Title = Clean(scan.Title);
        scan.Contractor = Clean(scan.Contractor);
        scan.Rank = RankRegex().Match(scan.Title) is { Success: true } rank ? Clean(rank.Groups[1].Value) : Clean(scan.Rank);
        foreach (var leg in scan.Cargo)
        {
            leg.Commodity = CleanClause(leg.Commodity);
            leg.Pickup = CleanLocation(leg.Pickup);
            leg.Destination = CleanLocation(leg.Destination);
        }
        scan.PickupLocations = scan.Cargo.SelectMany(x => x.Pickup.Split(" + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(x => !IsUnknown(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        scan.DestinationLocations = scan.Cargo.Select(x => x.Destination).Where(x => !IsUnknown(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Validate(scan, scan.PickupLocations.Count);
    }

    public static ContractScan ReparseStored(ContractScan scan)
    {
        // Parser rules improve over time. Reuse the already stored OCR text on
        // startup so old screenshots benefit without another Windows OCR pass.
        // Never overwrite fields the player has manually corrected.
        if (scan.ManuallyCorrected || string.IsNullOrWhiteSpace(scan.RawOcrText))
        {
            Revalidate(scan);
            return scan;
        }

        var refreshed = Parse(scan.RawOcrText, scan.SourceFile);
        refreshed.CapturedAtUtc = scan.CapturedAtUtc == default ? refreshed.CapturedAtUtc : scan.CapturedAtUtc;
        refreshed.SelectedForPlan = scan.SelectedForPlan;
        refreshed.ManualOverride = scan.ManualOverride;
        refreshed.ManuallyCorrected = scan.ManuallyCorrected;
        refreshed.RecoveredFields = scan.RecoveredFields
            .Concat(refreshed.RecoveredFields)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return refreshed;
    }

    private static string BuildCargoSignature(ContractScan scan)
    {
        var cargo = scan.Cargo.Select(x => string.Join("|",
                NormalizeSignatureValue(x.Commodity), x.Scu,
                NormalizeSignatureValue(x.Pickup), NormalizeSignatureValue(x.Destination)))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        return $"{scan.MaxContainerScu?.ToString(CultureInfo.InvariantCulture) ?? "?"}::{string.Join("::", cargo)}";
    }

    private static string NormalizeSignatureValue(string? value) =>
        Regex.Replace((value ?? "").Replace("&", "and", StringComparison.OrdinalIgnoreCase), @"[^a-z0-9]+", "", RegexOptions.IgnoreCase).ToUpperInvariant();

    private static void Validate(ContractScan scan, int pickupCount)
    {
        scan.Warnings.Clear();
        var evidence = 0;
        if (!IsUnknown(scan.Title)) evidence++;
        if (scan.Reward >= 1_000) evidence++;
        if (scan.Cargo.Count > 0 && scan.TotalScu > 0) evidence++;
        if (scan.Cargo.Any(x => !IsUnknown(x.Pickup))) evidence++;
        if (scan.Cargo.All(x => !IsUnknown(x.Destination)) && scan.Cargo.Count > 0) evidence++;
        if (scan.AvailabilityMinutes is > 0 || scan.ContractDeadlineNotApplicable) evidence++;
        scan.Confidence = evidence / 6.0;

        if (IsUnknown(scan.Title)) scan.Warnings.Add("Mission title was not read.");
        if (scan.Reward <= 0) scan.Warnings.Add("Reward was not read.");
        else if (scan.Reward < 1_000) scan.Warnings.Add($"Suspicious reward ({scan.Reward:N0} aUEC); likely OCR separator loss.");
        if (scan.AvailabilityMinutes is not > 0 && !scan.ContractDeadlineNotApplicable)
            scan.Warnings.Add("Contract availability timer was not read.");
        if (scan.Cargo.Count == 0) scan.Warnings.Add("No delivery objectives were parsed.");
        if (scan.Cargo.Any(x => IsUnknown(x.Commodity))) scan.Warnings.Add("A cargo name is missing or ambiguous.");
        if (scan.Cargo.Any(x => x.Scu <= 0)) scan.Warnings.Add("One or more cargo quantities are zero or unreadable.");
        if (scan.Cargo.Any(x => IsUnknown(x.Pickup))) scan.Warnings.Add("A pickup location is missing or ambiguous.");
        if (scan.Cargo.Any(x => IsUnknown(x.Destination))) scan.Warnings.Add("A destination is missing or ambiguous.");
        if (pickupCount > 1 && scan.Cargo.Any(x => IsUnknown(x.Pickup)))
            scan.Warnings.Add("Multiple pickups were seen but could not be matched safely to every commodity.");
        if (scan.TotalScu > 2_000) scan.Warnings.Add($"Suspicious cargo total ({scan.TotalScu:N0} SCU).");
        scan.IsComplete = scan.Confidence >= 0.99 && scan.Warnings.Count == 0;
        var recoveryNote = scan.RecoveredFields.Count > 0 ? $" {string.Join(" ", scan.RecoveredFields)}" : "";
        scan.Notes = (scan.Warnings.Count == 0 ? "Local OCR parsed all essential fields." : string.Join(" ", scan.Warnings)) + recoveryNote;
    }

    private static List<(string Commodity, string Location)> ParsePickups(string joined)
    {
        var result = new List<(string, string)>();
        foreach (Match match in CollectRegex().Matches(joined))
        {
            var item = (CleanClause(match.Groups["commodity"].Value), CleanLocation(match.Groups["pickup"].Value));
            // The same facility can supply several commodities. De-duplicate the
            // relationship, not merely the location, or later commodities lose
            // their pickup association.
            if (!IsUnknown(item.Item2) && !result.Any(x =>
                    x.Item1.Equals(item.Item1, StringComparison.OrdinalIgnoreCase) &&
                    x.Item2.Equals(item.Item2, StringComparison.OrdinalIgnoreCase)))
                result.Add(item);
        }
        return result;
    }

    private static int FindReward(string header)
    {
        // Windows OCR occasionally turns a header such as 108,500 into 108.5m.
        // In this UI the header is an exact aUEC amount, not a compact "million"
        // suffix, so reconstruct the lost hundreds before generic digit stripping.
        var compact = RewardOcrDecimalRegex().Match(header);
        if (compact.Success)
        {
            var whole = ParseNumber(compact.Groups["whole"].Value);
            var fractionText = Regex.Replace(compact.Groups["fraction"].Value, @"\D", "");
            if (whole is >= 10 and <= 999 && fractionText.Length is >= 1 and <= 3)
            {
                var fraction = int.Parse(fractionText.PadRight(3, '0'), CultureInfo.InvariantCulture);
                return whole * 1_000 + fraction;
            }
        }
        var match = RewardRegex().Match(header);
        if (match.Success) return ParseNumber(match.Groups["value"].Value);
        var lines = header.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("Reward", StringComparison.OrdinalIgnoreCase)) continue;
            var sameLine = Regex.Match(lines[i], @"Reward\D{0,20}(?<value>[0-9OIL][0-9OIL,.'’]{2,})", RegexOptions.IgnoreCase);
            if (sameLine.Success) return ParseNumber(sameLine.Groups["value"].Value);
            for (var j = i + 1; j < Math.Min(lines.Length, i + 3); j++)
            {
                var number = NumberRegex().Match(lines[j]);
                if (number.Success && ParseNumber(number.Value) >= 1_000) return ParseNumber(number.Value);
            }
        }
        return 0;
    }

    private static int? FindMaxContainer(string joined)
    {
        var match = MaxContainerRegex().Match(joined);
        return match.Success ? ParseNumber(match.Groups["value"].Value) : null;
    }

    private static int? FindAvailabilityMinutes(string header, string listEdge, int reward)
    {
        var direct = ParseDurationText(header, requireAvailabilityLabel: true);
        if (direct is > 0) return direct;

        // Some game states omit the timer from the large header while it remains on
        // the selected offer card at the cropped left edge. Associate it only when
        // the header reward maps to one unambiguous abbreviated reward/timer pair.
        if (reward < 1_000) return null;
        var lines = listEdge.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var rewardMatch = AbbreviatedRewardRegex().Match(lines[i]);
            if (!rewardMatch.Success) continue;
            var abbreviated = ParseNumber(rewardMatch.Groups["value"].Value) * 1_000;
            if (Math.Abs(reward - abbreviated) >= 1_000) continue;
            for (var j = i + 1; j < Math.Min(lines.Length, i + 3); j++)
            {
                var timer = ParseDurationText(lines[j], requireAvailabilityLabel: false);
                if (timer is > 0) { matches.Add(timer.Value); break; }
            }
        }
        return matches.Distinct().Count() == 1 ? matches[0] : null;
    }

    private static int? ParseDurationText(string text, bool requireAvailabilityLabel)
    {
        var input = requireAvailabilityLabel ? text : $"Contract Availability {text}";
        var match = AvailabilityHoursRegex().Match(input);
        if (match.Success)
        {
            var hours = ParseNumber(match.Groups["hours"].Value);
            var minutes = match.Groups["minutes"].Success ? ParseNumber(match.Groups["minutes"].Value) : 0;
            var total = hours * 60 + minutes;
            return total > 0 ? total : null;
        }
        match = AvailabilityMinutesRegex().Match(input);
        if (match.Success)
        {
            var minutes = ParseNumber(match.Groups["minutes"].Value);
            return minutes > 0 ? minutes : null;
        }
        return null;
    }

    private static string FindTitle(string header)
    {
        var match = TitleRegex().Match(header);
        return match.Success ? Clean(match.Value) : "Unknown contract";
    }

    private static string? FindAfterLabel(string joined, string label)
    {
        var match = Regex.Match(joined, $@"{Regex.Escape(label)}\s+(?<value>.+?)(?=\s+(?:DETAILS|PRIMARY|Reward|Contract Availability)\b|$)", RegexOptions.IgnoreCase);
        return match.Success ? CleanClause(match.Groups["value"].Value) : null;
    }

    private static string GetSection(string text, string name)
    {
        var match = Regex.Match(text, $@"\[{Regex.Escape(name)}\]\s*(?<content>.*?)(?=\n\[[A-Z_]+\]|$)", RegexOptions.Singleline);
        return match.Success ? match.Groups["content"].Value.Trim() : text;
    }

    public static int ParseNumber(string? value)
    {
        var normalized = (value ?? "").ToUpperInvariant().Replace('O', '0').Replace('I', '1').Replace('L', '1');
        normalized = Regex.Replace(normalized, @"[^0-9]", "");
        return int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static string NormalizeText(string? value)
    {
        value ??= "";
        value = value.Replace('\r', '\n').Replace('’', '\'').Replace('–', '-').Replace('—', '-').Replace('|', 'I');
        value = Regex.Replace(value, @"[ \t]+", " ");
        value = Regex.Replace(value, @"\n+", "\n");
        value = Regex.Replace(value, @"S\s*C\s*U", "SCU", RegexOptions.IgnoreCase);
        return value.Trim();
    }

    private static string Clean(string? value) => Regex.Replace(value ?? "", @"\s+", " ").Trim(' ', '-', ':', '.', ',');

    private static string CleanClause(string? value) => Clean(Regex.Replace(value ?? "",
        @"\s+(?:PRIMARY OBJECTIVES|DETAILS|Collect|Deliver|Reward|Contract Availability|Contracted By)\b.*$", "", RegexOptions.IgnoreCase));

    private static string CleanLocation(string? value)
    {
        value = CleanClause(value);
        value = Regex.Replace(value, @"\s+(?:Collect|Deliver|Reward|Contract Availability|Contracted By|ACCEPT OFFER)\b.*$", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\.\s+(?:Col\w*|el\b.*)$", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"Had[I1l]ey", "Hadley", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bCassi[A-Za-z0-9]{2,6}\b", "Cassillo", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"Reclamation\s+(?:[6B8&]|and)?\s*Disposal", "Reclamation & Disposal", RegexOptions.IgnoreCase);
        return Clean(value);
    }

    private static bool CommodityMatches(string? left, string? right)
    {
        left ??= "";
        right ??= "";
        return left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
               left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
               right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnknown(string? value) => string.IsNullOrWhiteSpace(value) || value.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"Reward\s+(?<value>[0-9OIL][0-9OIL,.'’]{2,})", RegexOptions.IgnoreCase)]
    private static partial Regex RewardRegex();
    [GeneratedRegex(@"Reward\D{0,20}(?<whole>[0-9OIL]{2,3})[.,](?<fraction>[0-9OIL]{1,3})\s*[mk]\b", RegexOptions.IgnoreCase)]
    private static partial Regex RewardOcrDecimalRegex();
    [GeneratedRegex(@"Contract\s+Availability\s+(?<hours>[0-9OIL]{1,2})\s*h(?:\s*(?<minutes>[0-9OIL]{1,3})\s*m)?", RegexOptions.IgnoreCase)]
    private static partial Regex AvailabilityHoursRegex();
    [GeneratedRegex(@"Contract\s+Availability\s+(?<minutes>[0-9OIL]{1,3})\s*m", RegexOptions.IgnoreCase)]
    private static partial Regex AvailabilityMinutesRegex();
    [GeneratedRegex(@"(?<value>[0-9OIL]{2,3})\s*k", RegexOptions.IgnoreCase)]
    private static partial Regex AbbreviatedRewardRegex();
    [GeneratedRegex(@"[0-9OIL][0-9OIL,.'’]{2,}")]
    private static partial Regex NumberRegex();
    [GeneratedRegex(@"(?:at\s+most\s+)?(?<value>[0-9OIL]{1,3})\s*SCU\s*(?:or\s+smaller|in\s+size|maximum|max(?:imum)?\s+container)", RegexOptions.IgnoreCase)]
    private static partial Regex MaxContainerRegex();
    [GeneratedRegex(@"(?:Rookie|Junior|Member|Experienced|Senior|Master)\s+Rank\s*-\s*(?:Direct\s+)?(?:Extra\s+Small|Small|Medium|Large)\s+Cargo\s+Haul", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();
    [GeneratedRegex(@"(?<rank>(?:Rookie|Junior|Member|Experienced|Senior|Master)\s+Rank)", RegexOptions.IgnoreCase)]
    private static partial Regex RankRegex();
    [GeneratedRegex(@"Collect\s+(?<commodity>.+?)\s+from\s+(?<pickup>.+?)(?=\s+(?:Collect|Deliver|Reward|Contract Availability|Contracted By|ACCEPT OFFER)\b|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CollectRegex();
    [GeneratedRegex(@"Deliver\s+(?:[0-9OIL]+\s*/\s*)?(?<scu>[0-9OIL][0-9OIL\s,.'’]{0,8})\s*SCU\s+of\s+(?<commodity>.+?)\s+to\s+(?<destination>.+?)(?=\s+(?:Collect|Deliver|Reward|Contract Availability|Contracted By|ACCEPT OFFER)\b|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DeliverRegex();
    [GeneratedRegex(@"Deliver\s+(?:[0-9OIL]+\s*/\s*)?(?<scu>[0-9OIL]{1,5})\s*SCU\s+(?<commodity>.+?)\s+to\s+(?<destination>.+?)(?=\s+(?:Collect|Deliver|Reward|Contract Availability|Contracted By|ACCEPT OFFER)\b|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LooseDeliverRegex();
    [GeneratedRegex(@"[Oo0]\s+[0-9OIL]+\s*/\s*(?<scu>[0-9OIL]{1,5})\s*SCU\s+of\s+(?<commodity>.+?)\s+to\s+(?<destination>.+?)(?=\s+(?:[Oo0]\s+)?(?:Collect|Deliver)\b|\s+ACCEPT OFFER\b|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MissingVerbDeliverRegex();
    [GeneratedRegex(@"(?:PRIMARY\s+OBJECTIVES\s+)?[Oo0]\s+SCU\s+of\s+(?<commodity>.+?)\s+to\s+(?<destination>.+?)(?=\s+(?:[Oo0]\s+)?(?:Collect|Deliver)\b|\s+ACCEPT OFFER\b|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MissingQuantityDeliverRegex();
}
