namespace FreightCopilot;

public static class TradeMath
{
    public static double Investment(int scu, double priceBuy) => scu * priceBuy;

    public static double Revenue(int scu, double priceSell) => scu * priceSell;

    public static double GrossProfit(int scu, double priceBuy, double priceSell) => Revenue(scu, priceSell) - Investment(scu, priceBuy);

    public static double NetProfit(int scu, double priceBuy, double priceSell, double? operatingCosts) =>
        GrossProfit(scu, priceBuy, priceSell) - (operatingCosts ?? 0);

    // Profit as a percentage of the money tied up in cargo; null when nothing was invested.
    public static double? Roi(double profit, double investment) => investment > 0 ? profit / investment * 100 : null;

    // Null when no time was recorded, so an untimed run never shows an invented hourly rate.
    public static double? ProfitPerHour(double profit, int? seconds) => seconds is > 0 ? profit / (seconds.Value / 3600d) : null;

    public static int? TotalSeconds(TradeRecord trade)
    {
        var parts = new[] { trade.LoadingSeconds, trade.TravelSeconds, trade.UnloadingSeconds };
        return parts.Any(x => x is > 0) ? parts.Sum(x => Math.Max(0, x ?? 0)) : null;
    }

    public static double? Gross(TradeRecord trade) => trade.IsCompleted ? trade.SaleRevenue - Investment(trade.Scu, trade.PriceBuy) : null;

    public static double? Net(TradeRecord trade) => Gross(trade) is { } gross ? gross - (trade.OperatingCosts ?? 0) : null;
}

public sealed record TradeGroup(string Name, int Runs, int Scu, double Profit, double? ProfitPerHour);

public sealed record TradeStatistics(int Runs, int OpenRuns, int Scu, double GrossProfit, double NetProfit, double Investment,
    double? ProfitPerHour, IReadOnlyList<TradeGroup> ByCommodity, IReadOnlyList<TradeGroup> ByRoute, IReadOnlyList<TradeGroup> ByDestination)
{
    // Only sold cargo counts. An open run has cost money but has no profit yet.
    public static TradeStatistics From(IReadOnlyCollection<TradeRecord> trades)
    {
        var done = trades.Where(x => x.IsCompleted).ToList();
        return new TradeStatistics(done.Count, trades.Count - done.Count, done.Sum(x => x.Scu),
            done.Sum(x => TradeMath.Gross(x) ?? 0), done.Sum(x => TradeMath.Net(x) ?? 0),
            done.Sum(x => TradeMath.Investment(x.Scu, x.PriceBuy)), HourlyRate(done),
            Group(done, x => x.CommodityName), Group(done, x => x.Route), Group(done, x => x.DestinationName));
    }

    private static List<TradeGroup> Group(List<TradeRecord> done, Func<TradeRecord, string> key) =>
        done.GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new TradeGroup(x.Key, x.Count(), x.Sum(t => t.Scu), x.Sum(t => TradeMath.Net(t) ?? 0), HourlyRate(x.ToList())))
            .OrderByDescending(x => x.Profit).ToList();

    // Runs without recorded times are left out of both the profit and the hours, so they cannot inflate the rate.
    private static double? HourlyRate(List<TradeRecord> done)
    {
        var timed = done.Where(x => TradeMath.TotalSeconds(x) is > 0).ToList();
        return TradeMath.ProfitPerHour(timed.Sum(x => TradeMath.Net(x) ?? 0), timed.Sum(x => TradeMath.TotalSeconds(x) ?? 0));
    }
}
