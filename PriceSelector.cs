namespace FreightCopilot;

public enum Confidence { Stale, Unknown, Low, Medium, High }

// A price for one side of a run and where it came from.
public sealed record PriceQuote(double Price, PriceSource Source, DateTime? AsOfUtc, double? Scu = null, string? ContainerSizes = null, bool Stale = false)
{
    public bool IsLocal => Source != PriceSource.Uex;
}

public static class PriceSelector
{
    // Chooses between the UEX report and the player's own confirmed observation of the same price.
    // In combined mode the newer of the two wins, and an own price past localMaxAge never replaces UEX.
    public static PriceQuote? Pick(PriceMode mode, PriceQuote? uex, PriceQuote? local, DateTime nowUtc, TimeSpan localMaxAge)
    {
        if (local is not null && (local.AsOfUtc is not { } seen || nowUtc - seen > localMaxAge)) local = local with { Stale = true };
        if (mode == PriceMode.UexOnly) return uex;
        if (local is null) return mode == PriceMode.LocalOnly ? null : uex;
        // Crate sizes belong to the terminal and hardly change, so UEX's fill in where the observation has none,
        // in every mode. Without them the planner would have to assume every size is on offer.
        if (string.IsNullOrWhiteSpace(local.ContainerSizes)) local = local with { ContainerSizes = uex?.ContainerSizes };
        if (mode == PriceMode.LocalOnly || uex is null) return local;
        if (local.Stale || uex.AsOfUtc > local.AsOfUtc) return uex;
        // The terminal rarely shows its stock next to a sell price, so UEX's figure stands in.
        return local with { Scu = local.Scu ?? uex.Scu };
    }

    public static Confidence Rate(PriceQuote quote, DateTime nowUtc)
    {
        if (quote.Stale) return Confidence.Stale;
        if (quote.AsOfUtc is not { } seen) return Confidence.Unknown;
        var age = nowUtc - seen;
        if (quote.IsLocal) return age <= TimeSpan.FromHours(12) ? Confidence.High : Confidence.Medium;
        return age <= TimeSpan.FromHours(24) ? Confidence.Medium : age <= TimeSpan.FromDays(7) ? Confidence.Low : Confidence.Stale;
    }

    // A run is only as trustworthy as its weaker price.
    public static Confidence Rate(PriceQuote buy, PriceQuote sell, DateTime nowUtc) =>
        (Confidence)Math.Min((int)Rate(buy, nowUtc), (int)Rate(sell, nowUtc));
}

// The newest confirmed own observation per terminal, commodity and side.
public sealed class LocalPrices
{
    private readonly Dictionary<(int Terminal, int Commodity, bool Buy), MarketObservation> _latest = [];

    public LocalPrices(IEnumerable<MarketObservation> observations)
    {
        foreach (var observation in observations.Where(x => x.Verified && x.Source != PriceSource.Uex && x.Price is > 0))
        {
            var key = (observation.TerminalId, observation.CommodityId, observation.IsBuy);
            if (!_latest.TryGetValue(key, out var known) || observation.ObservedUtc > known.ObservedUtc) _latest[key] = observation;
        }
    }

    public MarketObservation? Find(int terminalId, int commodityId, bool buy) => _latest.GetValueOrDefault((terminalId, commodityId, buy));

    public PriceQuote? Quote(int terminalId, int commodityId, bool buy) => Find(terminalId, commodityId, buy) is { } found
        ? new PriceQuote(found.Price!.Value, found.Source, found.ObservedUtc, buy ? found.StockScu : found.DemandScu, found.ContainerSizes)
        : null;

    public IEnumerable<MarketObservation> BoughtAt(int terminalId) => _latest.Values.Where(x => x.IsBuy && x.TerminalId == terminalId);

    public IEnumerable<MarketObservation> SoldElsewhere(int commodityId, int terminalId) =>
        _latest.Values.Where(x => !x.IsBuy && x.CommodityId == commodityId && x.TerminalId != terminalId);
}

public static class ObservationValidator
{
    public static readonly int[] ContainerSizes = [1, 2, 4, 8, 16, 24, 32];
    private const double MaximumPrice = 10_000_000;
    private const int MaximumScu = 10_000_000;

    // Reasons a row may not be saved as confirmed or sent to UEX.
    public static List<string> Errors(MarketObservation row, DateTime nowUtc)
    {
        var errors = new List<string>();
        if (row.TerminalId <= 0) errors.Add("Pick the terminal.");
        if (row.CommodityId <= 0) errors.Add("Pick a commodity that UEX knows.");
        var buy = row.PriceBuy is > 0;
        var sell = row.PriceSell is > 0;
        if (buy && sell) errors.Add("Give a buy or a sell price, not both; add a second row for the other side.");
        else if (!buy && !sell) errors.Add("Enter a buy or a sell price.");
        if (new[] { row.PriceBuy, row.PriceSell }.Any(x => x is { } price && (price < 0 || price > MaximumPrice || !double.IsFinite(price))))
            errors.Add("The price is out of range.");
        if (new[] { row.StockScu, row.DemandScu }.Any(x => x is < 0 or > MaximumScu)) errors.Add("The SCU figure is out of range.");
        if (row.Status is < 1 or > 7) errors.Add("The stock status must be 1 to 7.");
        if (NormalizeSizes(row.ContainerSizes) is null) errors.Add("Container sizes must be from 1, 2, 4, 8, 16, 24, 32.");
        if (row.ObservedUtc > nowUtc.AddMinutes(5)) errors.Add("The observation time is in the future.");
        return errors;
    }

    // A large gap to UEX is more often a misread digit than a market move.
    public static string? PriceWarning(double? observed, double? uex)
    {
        if (observed is not > 0 || uex is not > 0) return null;
        var change = (observed.Value - uex.Value) / uex.Value * 100;
        return Math.Abs(change) >= 30 ? $"{change:+0;-0}% against UEX — check the digits" : null;
    }

    // UEX has the commodity at the terminal on the other side only. That is a price under the wrong column far
    // more often than a terminal that changed what it trades, and UEX holds such a report back as "price_swapped".
    public static string? SideWarning(MarketObservation row, UexPrice? uex) =>
        row.Price is > 0 && uex is not null && uex.Quote(row.IsBuy) is null && uex.Quote(!row.IsBuy) is not null
            ? $"UEX has this here only as something you {(row.IsBuy ? "sell" : "buy")} — check the side" : null;

    // "8, 1,2" becomes "1,2,8". Null when a size does not exist in the game.
    public static string? NormalizeSizes(string? text)
    {
        var sizes = new SortedSet<int>();
        foreach (var part in (text ?? "").Split([',', ' ', ';', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var size) || !ContainerSizes.Contains(size)) return null;
            sizes.Add(size);
        }
        return string.Join(",", sizes);
    }
}
