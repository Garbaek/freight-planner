namespace FreightCopilot;

// SizesKnown is false when neither UEX nor the player has the crate sizes for one end, so every size was assumed.
public sealed record TradeIdea(UexRoute Route, PriceQuote Buy, PriceQuote Sell, CrateLoad Load, double Profit, bool SizesKnown);

// The runs worth showing, best profit first, and how many more were left out for selling in another star system.
public sealed record TradeIdeaList(List<TradeIdea> Ideas, int Elsewhere);

// Turns UEX's routes out of a station, and the player's own confirmed prices, into runs for one ship.
// In UEX-only mode, and whenever the player has no observations, the runs are exactly UEX's.
public static class TradeIdeaBuilder
{
    private static readonly int[] CrateSizes = [1, 2, 4, 8, 16, 24, 32];

    public static TradeIdeaList Build(UexTerminal origin, IEnumerable<UexRoute> routes, ShipProfile ship, bool otherSystems, PriceMode mode,
        LocalPrices local, Func<int, int, UexPrice?> uexPrice, DateTime nowUtc, TimeSpan localMaxAge)
    {
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
                ideas.Add(new TradeIdea(route, buy, sell, load, load.Scu * margin, !string.IsNullOrWhiteSpace(buy.ContainerSizes) && !string.IsNullOrWhiteSpace(sell.ContainerSizes)));
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
