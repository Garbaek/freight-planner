namespace FreightCopilot.Tests;

public class TradeIdeaTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 30, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(48);
    private static readonly ShipProfile Nomad = new("Consolidated Outland Nomad", 24, [new CargoGrid(3, 2, 4)]);
    private static readonly UexTerminal PssLambda = new() { Id = 843, Nickname = "PSS Lambda", StarSystemName = "Nyx" };
    private const int Levski = 778, Area18 = 12, Compboard = 18, Dymantium = 127;

    // What UEX had on 10 October 2026: Compboard at PSS Lambda reported six days earlier, at Levski two days earlier.
    private static readonly Dictionary<(int, int), UexPrice> Uex = new()
    {
        [(843, Compboard)] = new UexPrice { IdTerminal = 843, IdCommodity = Compboard, PriceBuy = 28_104, ScuBuy = 60, ContainerSizes = "1,2", DateModified = Unix(Now.AddHours(-154)) },
        [(Levski, Compboard)] = new UexPrice { IdTerminal = Levski, IdCommodity = Compboard, PriceSell = 34_000, ScuSell = 88, ContainerSizes = "1,2,4,8,16,24,32", DateModified = Unix(Now.AddHours(-55)) }
    };

    private static long Unix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    private static UexRoute Route(int commodity, string name, int destination, double buy, double sell, double stock, double demand, string sizesHere, string sizesThere,
        string system = "Nyx", double distance = 62) => new()
    {
        IdCommodity = commodity, CommodityName = name, IdTerminalDestination = destination, PriceOrigin = buy, PriceDestination = sell, ScuOrigin = stock,
        ScuDestination = demand, ContainerSizesOrigin = sizesHere, ContainerSizesDestination = sizesThere, Distance = distance,
        OriginStarSystemName = "Nyx", DestinationStarSystemName = system
    };

    private static readonly UexRoute[] Routes =
    [
        Route(Compboard, "Compboard", Levski, 28_104, 34_000, 60, 88, "1,2", "1,2,4,8,16,24,32"),
        Route(Dymantium, "Dymantium", Levski, 20_528, 25_000, 51, 0, "1,2,4,8", "1,2,4,8,16,24,32"),
        Route(Compboard, "Compboard", Area18, 28_104, 36_000, 60, 300, "1,2", "1,2,4,8,16,24,32", system: "Stanton", distance: 900),
        Route(10, "Audio-Visual Equipment", Levski, 27_965, 27_000, 10, 50, "1,2", "1,2")
    ];

    private static MarketObservation Mine(int terminal, string name, double? buy, double? sell, double hoursAgo, int? stock = null, string system = "Nyx") => new()
    {
        StarSystem = system, TerminalId = terminal, TerminalName = name, CommodityId = Compboard, CommodityName = "Compboard", PriceBuy = buy, PriceSell = sell,
        StockScu = stock, ObservedUtc = Now.AddHours(-hoursAgo), Source = PriceSource.Screenshot, Verified = true
    };

    private static TradeIdeaList Build(PriceMode mode, IEnumerable<MarketObservation> observations, bool otherSystems = false, ShipProfile? ship = null,
        IEnumerable<UexRoute>? routes = null) =>
        TradeIdeaBuilder.Build(PssLambda, routes ?? Routes, ship ?? Nomad, otherSystems, mode, new LocalPrices(observations), (t, c) => Uex.GetValueOrDefault((t, c)), Now, MaxAge);

    [Fact]
    public void Uex_only_gives_uexs_own_runs()
    {
        var (ideas, elsewhere) = Build(PriceMode.UexOnly, [Mine(843, "PSS Lambda", 28_114, null, 1), Mine(Levski, "Levski", null, 34_650, 1)]);

        Assert.Equal(1, elsewhere);
        Assert.Equal(["Compboard", "Dymantium"], ideas.Select(x => x.Route.CommodityName));
        var compboard = ideas[0];
        Assert.Equal(28_104, compboard.Buy.Price);
        Assert.Equal(34_000, compboard.Sell.Price);
        Assert.Equal(24, compboard.Load.Scu);
        Assert.Equal("12 × 2 SCU", compboard.Load.Crates);
        Assert.Equal(24 * 5_896, compboard.Profit);
        Assert.False(compboard.Buy.IsLocal || compboard.Sell.IsLocal);
        // The price dates come from UEX's price list, not from the route table.
        Assert.Equal(Now.AddHours(-154), compboard.Buy.AsOfUtc);
        Assert.Equal(Confidence.Low, PriceSelector.Rate(compboard.Buy, compboard.Sell, Now));
    }

    [Fact]
    public void Without_own_observations_combined_mode_equals_uex_only()
    {
        var uexOnly = Build(PriceMode.UexOnly, []);
        var combined = Build(PriceMode.Combined, []);

        Assert.Equal(uexOnly.Elsewhere, combined.Elsewhere);
        Assert.Equal(uexOnly.Ideas.Select(x => (x.Route.CommodityName, x.Buy.Price, x.Sell.Price, x.Load.Scu, x.Load.Crates, x.Profit)),
            combined.Ideas.Select(x => (x.Route.CommodityName, x.Buy.Price, x.Sell.Price, x.Load.Scu, x.Load.Crates, x.Profit)));
    }

    [Fact]
    public void Newer_own_prices_replace_uexs_and_say_so()
    {
        var compboard = Build(PriceMode.Combined, [Mine(843, "PSS Lambda", 28_114, null, 1), Mine(Levski, "Levski", null, 34_650, 0.5)]).Ideas[0];

        Assert.Equal(28_114, compboard.Buy.Price);
        Assert.Equal(34_650, compboard.Sell.Price);
        Assert.True(compboard.Buy.IsLocal && compboard.Sell.IsLocal);
        Assert.Equal(24 * 6_536, compboard.Profit);
        Assert.Equal(Confidence.High, PriceSelector.Rate(compboard.Buy, compboard.Sell, Now));
        // The screenshots showed no stock or crate sizes for Compboard, so UEX's are kept: 1 and 2 SCU crates only.
        Assert.Equal("12 × 2 SCU", compboard.Load.Crates);
    }

    [Fact]
    public void One_own_price_is_combined_with_uexs_other_side()
    {
        var compboard = Build(PriceMode.Combined, [Mine(Levski, "Levski", null, 34_650, 0.5)]).Ideas[0];

        Assert.Equal(28_104, compboard.Buy.Price);
        Assert.False(compboard.Buy.IsLocal);
        Assert.Equal(34_650, compboard.Sell.Price);
        Assert.True(compboard.Sell.IsLocal);
        Assert.Equal(Confidence.Low, PriceSelector.Rate(compboard.Buy, compboard.Sell, Now));
    }

    [Fact]
    public void Old_own_prices_no_longer_replace_uex()
    {
        var compboard = Build(PriceMode.Combined, [Mine(843, "PSS Lambda", 28_114, null, 300), Mine(Levski, "Levski", null, 34_650, 300)]).Ideas[0];
        Assert.Equal(28_104, compboard.Buy.Price);
        Assert.Equal(34_000, compboard.Sell.Price);
    }

    [Fact]
    public void An_observed_stock_limits_the_load()
    {
        var compboard = Build(PriceMode.Combined, [Mine(843, "PSS Lambda", 28_114, null, 1, stock: 7)]).Ideas.Single(x => x.Route.CommodityName == "Compboard");
        // Seven SCU on the shelf, in 1 and 2 SCU crates.
        Assert.Equal(7, compboard.Load.Scu);
        Assert.Equal("3 × 2 SCU + 1 × 1 SCU", compboard.Load.Crates);
    }

    [Fact]
    public void Local_only_shows_runs_seen_at_both_ends_only()
    {
        Assert.Empty(Build(PriceMode.LocalOnly, [Mine(843, "PSS Lambda", 28_114, null, 1)]).Ideas);

        var ideas = Build(PriceMode.LocalOnly, [Mine(843, "PSS Lambda", 28_114, null, 1), Mine(Levski, "Levski", null, 34_650, 1)]).Ideas;
        var compboard = Assert.Single(ideas);
        Assert.Equal(24 * 6_536, compboard.Profit);
        Assert.True(compboard.Buy.IsLocal && compboard.Sell.IsLocal);
        // The mode is about prices. Crate sizes belong to the terminal, so UEX's still apply: 1 and 2 SCU only.
        Assert.Equal("12 × 2 SCU", compboard.Load.Crates);
        Assert.True(compboard.SizesKnown);
    }

    [Fact]
    public void Unknown_crate_sizes_are_flagged_not_hidden()
    {
        var delta = Build(PriceMode.Combined, [Mine(843, "PSS Lambda", 28_114, null, 1), Mine(855, "PSS Delta", null, 30_000, 2)]).Ideas
            .Single(x => x.Route.IdTerminalDestination == 855);
        // Nobody has reported what crates PSS Delta takes Compboard in.
        Assert.False(delta.SizesKnown);
        Assert.True(Build(PriceMode.UexOnly, []).Ideas.All(x => x.SizesKnown));
    }

    [Fact]
    public void A_run_uex_does_not_list_appears_from_own_observations()
    {
        var ideas = Build(PriceMode.Combined, [Mine(843, "PSS Lambda", 28_114, null, 1), Mine(855, "PSS Delta", null, 30_000, 2)]).Ideas;

        var delta = ideas.Single(x => x.Route.IdTerminalDestination == 855);
        Assert.Equal("PSS Delta", delta.Route.DestinationTerminalName);
        Assert.Null(delta.Route.Distance);
        Assert.Equal(24 * 1_886, delta.Profit);
        Assert.DoesNotContain(Build(PriceMode.UexOnly, [Mine(843, "PSS Lambda", 28_114, null, 1), Mine(855, "PSS Delta", null, 30_000, 2)]).Ideas, x => x.Route.IdTerminalDestination == 855);
    }

    [Fact]
    public void Runs_to_another_system_are_counted_or_included()
    {
        Assert.Equal(1, Build(PriceMode.UexOnly, []).Elsewhere);
        var all = Build(PriceMode.UexOnly, [], otherSystems: true);
        Assert.Equal(0, all.Elsewhere);
        // Best profit first: Area 18 pays more than Levski.
        Assert.Equal(Area18, all.Ideas[0].Route.IdTerminalDestination);
    }

    [Fact]
    public void A_run_at_a_loss_is_never_offered() =>
        Assert.DoesNotContain(Build(PriceMode.Combined, [], otherSystems: true).Ideas, x => x.Route.CommodityName == "Audio-Visual Equipment");

    [Fact]
    public void An_own_price_can_turn_a_listed_run_into_a_loss()
    {
        var ideas = Build(PriceMode.Combined, [Mine(Levski, "Levski", null, 28_000, 0.5)]).Ideas;
        Assert.DoesNotContain(ideas, x => x.Route.CommodityName == "Compboard");
    }

    [Fact]
    public void A_ship_without_a_listed_grid_gets_a_theoretical_load()
    {
        var raft = new ShipProfile("ARGO RAFT", 192);
        var dymantium = Build(PriceMode.UexOnly, [], ship: raft).Ideas.Single(x => x.Route.CommodityName == "Dymantium");
        Assert.Equal(CrateBasis.Theoretical, dymantium.Load.Basis);
        Assert.Equal(51, dymantium.Load.Scu);
        Assert.Equal("up to 8 SCU each", dymantium.Load.Crates);
    }
}
