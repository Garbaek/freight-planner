namespace FreightCopilot;

public enum RouteRisk { Low, Medium, Unknown, High }

// Profit is the best case from the reported stock/demand. LikelyScu and ExpectedProfit temper it with
// UEX user-trade averages and this pilot's actual outcomes; they never pretend those community figures are live stock.
public sealed record TradeIdea(UexRoute Route, PriceQuote Buy, PriceQuote Sell, CrateLoad Load, double Profit, bool SizesKnown)
{
    public int LikelyScu { get; init; } = Load.Scu;
    public CrateLoad LikelyLoad { get; init; } = Load;
    public double ExpectedProfit { get; init; } = Profit;
    public RouteRisk Risk { get; init; } = RouteRisk.Unknown;
    public string RiskDetail { get; init; } = "No actual trade samples yet; treat stock and demand as an estimate.";
}

// The runs worth showing, best profit first, and how many more were left out for selling in another star system.
public sealed record TradeIdeaList(List<TradeIdea> Ideas, int Elsewhere);

// Turns UEX's routes out of a station, and the player's own confirmed prices, into runs for one ship.
// In UEX-only mode, and whenever the player has no observations, the runs are exactly UEX's.
public static class TradeIdeaBuilder
{
    private static readonly int[] CrateSizes = [1, 2, 4, 8, 16, 24, 32];

    public static TradeIdeaList Build(UexTerminal origin, IEnumerable<UexRoute> routes, ShipProfile ship, bool otherSystems, PriceMode mode,
        LocalPrices local, Func<int, int, UexPrice?> uexPrice, DateTime nowUtc, TimeSpan localMaxAge,
        IReadOnlyCollection<TradeRecord>? history = null)
    {
        history ??= [];
        var ideas = new List<TradeIdea>();
        var covered = new HashSet<(int Destination, int Commodity)>();
        var elsewhere = 0;

        foreach (var route in routes)
        {
            covered.Add((route.IdTerminalDestination, route.IdCommodity));
            // The route's own price, stock and crate sizes are used; only the report time is looked up,
            // because a route's date is when UEX rebuilt its route table, not when the price was seen.
            Consider(route,
                PriceSelector.Pick(mode, RouteQuote(route.PriceOrigin, route.ScuOrigin, route.ContainerSizesOrigin, origin.Id, route.IdCommodity),
                    local.Quote(origin.Id, route.IdCommodity, buy: true), nowUtc, localMaxAge),
                PriceSelector.Pick(mode, RouteQuote(route.PriceDestination, route.ScuDestination, route.ContainerSizesDestination, route.IdTerminalDestination, route.IdCommodity),
                    local.Quote(route.IdTerminalDestination, route.IdCommodity, buy: false), nowUtc, localMaxAge));
        }

        // Runs UEX does not list at all but the player has seen both ends of.
        if (mode != PriceMode.UexOnly)
            foreach (var bought in local.BoughtAt(origin.Id).ToList())
            foreach (var sold in local.SoldElsewhere(bought.CommodityId, origin.Id).Where(x => covered.Add((x.TerminalId, bought.CommodityId))).ToList())
                Consider(new UexRoute
                    {
                        IdCommodity = bought.CommodityId, IdTerminalDestination = sold.TerminalId, CommodityName = bought.CommodityName,
                        OriginStarSystemName = origin.StarSystemName, DestinationStarSystemName = sold.StarSystem, DestinationTerminalName = sold.TerminalName
                    },
                    PriceSelector.Pick(mode, uexPrice(origin.Id, bought.CommodityId)?.Quote(buy: true), local.Quote(origin.Id, bought.CommodityId, buy: true), nowUtc, localMaxAge),
                    PriceSelector.Pick(mode, uexPrice(sold.TerminalId, bought.CommodityId)?.Quote(buy: false), local.Quote(sold.TerminalId, bought.CommodityId, buy: false), nowUtc, localMaxAge));

        return new TradeIdeaList(ideas.OrderByDescending(x => x.Profit).ThenBy(x => x.Route.Distance ?? double.MaxValue).ToList(), elsewhere);

        void Consider(UexRoute route, PriceQuote? buy, PriceQuote? sell)
        {
            if (buy is null || sell is null) return;
            var margin = sell.Price - buy.Price;
            if (buy.Price <= 0 || margin <= 0) return;
            if (!string.Equals(route.OriginStarSystemName, route.DestinationStarSystemName, StringComparison.OrdinalIgnoreCase) && !otherSystems)
            {
                elsewhere++;
                return;
            }
            // Zero stock or demand means there is no figure, not that the shelf is empty.
            var scu = ship.CapacityScu;
            if (buy.Scu is > 0) scu = (int)Math.Min(scu, buy.Scu.Value);
            if (sell.Scu is > 0) scu = (int)Math.Min(scu, sell.Scu.Value);
            var sizes = Sizes(buy.ContainerSizes).Intersect(Sizes(sell.ContainerSizes)).Where(x => x <= scu).ToList();
            if (sizes.Count == 0) return;
            var load = CratePlanner.Plan(scu, sizes, ship.Grids);
            if (load.Scu > 0)
            {
                var evidence = new List<string>();
                var likelyRatio = 1d;
                AddUexEvidence(route.ScuOriginUsers, route.ScuOriginUsersRows, "bought", load.Scu);
                AddUexEvidence(route.ScuDestinationUsers, route.ScuDestinationUsersRows, "sold", load.Scu);

                var own = history.Where(x => x.OriginId == origin.Id && x.CommodityId == route.IdCommodity &&
                    x.PlannedDestinationId == route.IdTerminalDestination && x.PlannedScu > 0).ToList();
                if (own.Count > 0)
                {
                    var fill = own.Average(x => Math.Clamp(x.Scu / (double)x.PlannedScu, 0, 1));
                    var soldOutcomes = own.Where(x => x.Sales.Count > 0).ToList();
                    var accepted = soldOutcomes.Count == 0 ? (double?)null : soldOutcomes.Average(x => x.Scu > 0
                        ? Math.Clamp(x.Sales.Where(s => s.TerminalId == route.IdTerminalDestination).Sum(s => s.Scu) / (double)x.Scu, 0, 1)
                        : 0);
                    // Purchase fill is measured against the plan; buyer acceptance is measured against what
                    // was actually bought. Together they estimate how much of the original plan sold here.
                    likelyRatio = Math.Min(likelyRatio, fill * (accepted ?? 1));
                    evidence.Add($"Your {own.Count:N0} run{(own.Count == 1 ? "" : "s")}: bought {fill:P0} of plan" +
                        (accepted is { } share ? $"; this buyer accepted {share:P0} in {soldOutcomes.Count:N0} sale attempt{(soldOutcomes.Count == 1 ? "" : "s")}." : "."));
                }

                var volatility = new[] { route.VolatilityOrigin, route.VolatilityDestination }.Where(x => x is >= 0).DefaultIfEmpty().Max() ?? 0;
                if (volatility > 0) evidence.Add($"UEX price volatility: {volatility:P0}.");
                if (route.Score is { } score) evidence.Add($"UEX route score: {score:N0}.");

                var likelyLimit = Math.Max(1, (int)Math.Floor(load.Scu * likelyRatio));
                var likelyLoad = CratePlanner.Plan(likelyLimit, sizes, ship.Grids);
                var likelyScu = Math.Min(load.Scu, likelyLoad.Scu);
                var risk = evidence.Count == 0 ? RouteRisk.Unknown
                    : likelyScu < load.Scu * .55 || volatility >= .25 ? RouteRisk.High
                    : likelyScu < load.Scu * .85 || volatility >= .12 ? RouteRisk.Medium
                    : RouteRisk.Low;
                ideas.Add(new TradeIdea(route, buy, sell, load, load.Scu * margin,
                    !string.IsNullOrWhiteSpace(buy.ContainerSizes) && !string.IsNullOrWhiteSpace(sell.ContainerSizes))
                {
                    LikelyScu = likelyScu,
                    LikelyLoad = likelyLoad,
                    ExpectedProfit = likelyScu * margin,
                    Risk = risk,
                    RiskDetail = evidence.Count > 0 ? string.Join(" ", evidence) : "No actual trade samples yet; treat stock and demand as an estimate."
                });

                void AddUexEvidence(double? average, double? rows, string verb, int plannedScu)
                {
                    if (average is not > 0 || rows is not >= 2) return;
                    likelyRatio = Math.Min(likelyRatio, Math.Clamp(average.Value / plannedScu, 0, 1));
                    evidence.Add($"UEX user trades {verb} {average:N0} SCU on average ({rows:N0} samples). ");
                }
            }
        }

        PriceQuote? RouteQuote(double? price, double? scu, string? sizes, int terminalId, int commodityId) =>
            price is > 0 ? new PriceQuote(price.Value, PriceSource.Uex, uexPrice(terminalId, commodityId)?.ReportedUtc, scu, sizes) : null;
    }

    private static IEnumerable<int> Sizes(string? csv)
    {
        var sizes = (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var size) ? size : 0).Where(x => x > 0).ToList();
        return sizes.Count > 0 ? sizes : CrateSizes;
    }
}
