namespace FreightCopilot.Tests;

public class TradeMathTests
{
    // The Compboard run of 10 October 2026: bought at PSS Lambda, sold at Levski.
    private static TradeRecord Compboard(double? costs = null, int? loading = null, int? travel = null, int? unloading = null) => new()
    {
        Ship = "Consolidated Outland Nomad", OriginName = "PSS Lambda", DestinationName = "Levski", CommodityName = "Compboard",
        Scu = 6, PriceBuy = 28_114, PriceSell = 34_650, OperatingCosts = costs,
        LoadingSeconds = loading, TravelSeconds = travel, UnloadingSeconds = unloading,
        BoughtUtc = new DateTime(2026, 10, 10, 11, 34, 0, DateTimeKind.Utc), SoldUtc = new DateTime(2026, 10, 10, 12, 8, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void A_completed_run_adds_up_like_the_terminal_did()
    {
        Assert.Equal(168_684, TradeMath.Investment(6, 28_114));
        Assert.Equal(207_900, TradeMath.Revenue(6, 34_650));
        Assert.Equal(39_216, TradeMath.GrossProfit(6, 28_114, 34_650));
        Assert.Equal(23.25, TradeMath.Roi(39_216, 168_684)!.Value, 2);
    }

    [Fact]
    public void Operating_costs_come_off_the_net_profit_only()
    {
        var trade = Compboard(costs: 330);
        Assert.Equal(39_216, TradeMath.Gross(trade));
        Assert.Equal(38_886, TradeMath.Net(trade));
    }

    [Fact]
    public void An_unsold_run_has_no_profit()
    {
        var trade = Compboard();
        trade.PriceSell = null;
        trade.SoldUtc = null;
        Assert.False(trade.IsCompleted);
        Assert.Null(TradeMath.Gross(trade));
        Assert.Null(TradeMath.Net(trade));
    }

    [Fact]
    public void Profit_per_hour_needs_a_recorded_time()
    {
        Assert.Null(TradeMath.TotalSeconds(Compboard()));
        Assert.Null(TradeMath.ProfitPerHour(39_216, null));
        var timed = Compboard(loading: 132, travel: 1_589, unloading: 79);
        Assert.Equal(1_800, TradeMath.TotalSeconds(timed));
        Assert.Equal(78_432, TradeMath.ProfitPerHour(39_216, 1_800)!.Value, 6);
    }

    [Fact]
    public void Nothing_invested_gives_no_return_figure() => Assert.Null(TradeMath.Roi(100, 0));

    [Fact]
    public void Statistics_count_sold_cargo_only()
    {
        var open = Compboard();
        open.PriceSell = null;
        open.SoldUtc = null;
        var second = Compboard(costs: 330, loading: 100, travel: 1_600, unloading: 100);
        second.CommodityName = "Agricium";
        second.Scu = 24;
        second.PriceBuy = 8_379;
        second.PriceSell = 9_200;

        var statistics = TradeStatistics.From([Compboard(), open, second]);

        Assert.Equal(2, statistics.Runs);
        Assert.Equal(1, statistics.OpenRuns);
        Assert.Equal(30, statistics.Scu);
        Assert.Equal(39_216 + 24 * 821, statistics.GrossProfit);
        Assert.Equal(39_216 + 24 * 821 - 330, statistics.NetProfit);
        Assert.Equal(["Compboard", "Agricium"], statistics.ByCommodity.Select(x => x.Name));
        Assert.Equal("PSS Lambda → Levski", Assert.Single(statistics.ByRoute).Name);
        Assert.Equal("Levski", Assert.Single(statistics.ByDestination).Name);
    }

    [Fact]
    public void The_hourly_rate_ignores_runs_without_a_time()
    {
        var timed = Compboard(loading: 600, travel: 2_400, unloading: 600);
        var statistics = TradeStatistics.From([Compboard(), timed]);
        // Only the timed run counts: 39,216 in one hour, not 78,432.
        Assert.Equal(39_216, statistics.ProfitPerHour!.Value, 6);
    }
}
