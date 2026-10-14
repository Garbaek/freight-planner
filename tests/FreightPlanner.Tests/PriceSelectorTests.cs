namespace FreightCopilot.Tests;

public class PriceSelectorTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(48);

    private static PriceQuote Uex(double price, double hoursAgo, double? scu = null, string? sizes = null) =>
        new(price, PriceSource.Uex, Now.AddHours(-hoursAgo), scu, sizes);

    private static PriceQuote Mine(double price, double hoursAgo, double? scu = null, string? sizes = null) =>
        new(price, PriceSource.Screenshot, Now.AddHours(-hoursAgo), scu, sizes);

    [Fact]
    public void Uex_only_never_uses_an_own_price() =>
        Assert.Equal(PriceSource.Uex, PriceSelector.Pick(PriceMode.UexOnly, Uex(34_000, 54), Mine(34_650, 1), Now, MaxAge)!.Source);

    [Fact]
    public void Uex_only_has_no_price_where_uex_has_none() =>
        Assert.Null(PriceSelector.Pick(PriceMode.UexOnly, null, Mine(34_650, 1), Now, MaxAge));

    [Fact]
    public void Local_only_never_uses_uex()
    {
        Assert.Null(PriceSelector.Pick(PriceMode.LocalOnly, Uex(34_000, 1), null, Now, MaxAge));
        Assert.Equal(34_650, PriceSelector.Pick(PriceMode.LocalOnly, Uex(34_000, 1), Mine(34_650, 5), Now, MaxAge)!.Price);
    }

    [Fact]
    public void Combined_prefers_the_newer_own_price()
    {
        var picked = PriceSelector.Pick(PriceMode.Combined, Uex(34_000, 54), Mine(34_650, 1), Now, MaxAge)!;
        Assert.Equal(34_650, picked.Price);
        Assert.True(picked.IsLocal);
        Assert.False(picked.Stale);
    }

    [Fact]
    public void Combined_prefers_uex_when_its_report_is_newer() =>
        Assert.Equal(34_000, PriceSelector.Pick(PriceMode.Combined, Uex(34_000, 2), Mine(34_650, 20), Now, MaxAge)!.Price);

    [Fact]
    public void An_old_own_price_does_not_replace_uex() =>
        Assert.Equal(PriceSource.Uex, PriceSelector.Pick(PriceMode.Combined, Uex(34_000, 200), Mine(34_650, 49), Now, MaxAge)!.Source);

    [Fact]
    public void An_old_own_price_is_marked_stale_when_it_is_all_there_is()
    {
        Assert.True(PriceSelector.Pick(PriceMode.Combined, null, Mine(34_650, 49), Now, MaxAge)!.Stale);
        Assert.True(PriceSelector.Pick(PriceMode.LocalOnly, null, Mine(34_650, 49), Now, MaxAge)!.Stale);
        Assert.Equal(Confidence.Stale, PriceSelector.Rate(PriceSelector.Pick(PriceMode.LocalOnly, null, Mine(34_650, 49), Now, MaxAge)!, Now));
    }

    [Fact]
    public void Uex_fills_in_what_the_own_observation_did_not_show()
    {
        var picked = PriceSelector.Pick(PriceMode.Combined, Uex(28_104, 150, 60, "1,2"), Mine(28_114, 1), Now, MaxAge)!;
        Assert.Equal(28_114, picked.Price);
        Assert.Equal(60, picked.Scu);
        Assert.Equal("1,2", picked.ContainerSizes);
    }

    [Fact]
    public void Crate_sizes_come_from_uex_even_when_only_own_prices_are_used()
    {
        var picked = PriceSelector.Pick(PriceMode.LocalOnly, Uex(28_104, 150, 60, "1,2"), Mine(28_114, 1), Now, MaxAge)!;
        Assert.Equal(28_114, picked.Price);
        Assert.Equal("1,2", picked.ContainerSizes);
        // The stock figure is a volatile report like the price, so it is not borrowed in this mode.
        Assert.Null(picked.Scu);
    }

    [Fact]
    public void What_the_own_observation_showed_is_kept()
    {
        var picked = PriceSelector.Pick(PriceMode.Combined, Uex(8_379, 30, 211, "1,2,4,8,16"), Mine(8_379, 1, 84, "1,2,4,8"), Now, MaxAge)!;
        Assert.Equal(84, picked.Scu);
        Assert.Equal("1,2,4,8", picked.ContainerSizes);
    }

    [Theory]
    [InlineData(1, Confidence.High)]
    [InlineData(13, Confidence.Medium)]
    public void An_own_price_is_trusted_most_while_fresh(double hoursAgo, Confidence expected) =>
        Assert.Equal(expected, PriceSelector.Rate(Mine(100, hoursAgo), Now));

    [Theory]
    [InlineData(1, Confidence.Medium)]
    [InlineData(48, Confidence.Low)]
    [InlineData(24 * 8, Confidence.Stale)]
    public void A_uex_price_loses_trust_with_age(double hoursAgo, Confidence expected) =>
        Assert.Equal(expected, PriceSelector.Rate(Uex(100, hoursAgo), Now));

    [Fact]
    public void A_uex_price_without_a_date_is_unknown() =>
        Assert.Equal(Confidence.Unknown, PriceSelector.Rate(new PriceQuote(100, PriceSource.Uex, null), Now));

    [Fact]
    public void A_run_is_as_trustworthy_as_its_weaker_price() =>
        Assert.Equal(Confidence.Low, PriceSelector.Rate(Mine(28_114, 1), Uex(34_000, 54), Now));

    private static MarketObservation Seen(int terminal, int commodity, double? buy, double? sell, double hoursAgo, bool verified = true,
        PriceSource source = PriceSource.Screenshot) => new()
    {
        TerminalId = terminal, TerminalName = $"T{terminal}", CommodityId = commodity, CommodityName = $"C{commodity}",
        PriceBuy = buy, PriceSell = sell, ObservedUtc = Now.AddHours(-hoursAgo), Verified = verified, Source = source
    };

    [Fact]
    public void The_newest_confirmed_observation_is_the_local_price()
    {
        var prices = new LocalPrices([Seen(843, 18, 28_000, null, 30), Seen(843, 18, 28_114, null, 1), Seen(843, 18, 28_050, null, 10)]);
        Assert.Equal(28_114, prices.Quote(843, 18, buy: true)!.Price);
    }

    [Fact]
    public void Unconfirmed_and_uex_rows_are_never_a_local_price()
    {
        var prices = new LocalPrices([Seen(843, 18, 99_999, null, 1, verified: false), Seen(843, 18, 28_104, null, 2, source: PriceSource.Uex)]);
        Assert.Null(prices.Quote(843, 18, buy: true));
    }

    [Fact]
    public void Buy_and_sell_prices_are_kept_apart()
    {
        var prices = new LocalPrices([Seen(778, 18, null, 34_650, 1), Seen(843, 18, 28_114, null, 2)]);
        Assert.Null(prices.Quote(778, 18, buy: true));
        Assert.Equal(34_650, prices.Quote(778, 18, buy: false)!.Price);
        Assert.Equal(778, Assert.Single(prices.SoldElsewhere(18, 843)).TerminalId);
        Assert.Equal(18, Assert.Single(prices.BoughtAt(843)).CommodityId);
    }

    [Fact]
    public void A_valid_row_has_no_errors()
    {
        var row = Seen(843, 18, 28_114, null, 1);
        row.StockScu = 51;
        row.Status = 5;
        row.ContainerSizes = "1,2";
        Assert.Empty(ObservationValidator.Errors(row, Now));
    }

    [Fact]
    public void A_row_needs_a_terminal_a_commodity_and_exactly_one_price()
    {
        Assert.Contains(ObservationValidator.Errors(Seen(0, 18, 100, null, 1), Now), x => x.Contains("terminal"));
        Assert.Contains(ObservationValidator.Errors(Seen(843, 0, 100, null, 1), Now), x => x.Contains("commodity"));
        Assert.Contains(ObservationValidator.Errors(Seen(843, 18, null, null, 1), Now), x => x.Contains("Enter a buy or a sell price"));
        Assert.Contains(ObservationValidator.Errors(Seen(843, 18, 100, 120, 1), Now), x => x.Contains("not both"));
    }

    [Fact]
    public void Impossible_values_are_refused()
    {
        var row = Seen(843, 18, 28_114, null, 1);
        row.Status = 8;
        row.StockScu = -1;
        row.ContainerSizes = "1,3";
        var errors = ObservationValidator.Errors(row, Now);
        Assert.Contains(errors, x => x.Contains("status"));
        Assert.Contains(errors, x => x.Contains("SCU"));
        Assert.Contains(errors, x => x.Contains("Container"));
        Assert.Contains(ObservationValidator.Errors(Seen(843, 18, 28_114, null, -1), Now), x => x.Contains("future"));
    }

    [Theory]
    [InlineData("8, 1,2", "1,2,8")]
    [InlineData("16 24 32", "16,24,32")]
    [InlineData("", "")]
    [InlineData("1,3", null)]
    [InlineData("big", null)]
    public void Container_sizes_are_normalized(string text, string? expected) => Assert.Equal(expected, ObservationValidator.NormalizeSizes(text));

    [Fact]
    public void A_price_far_from_uex_gets_a_warning()
    {
        Assert.Null(ObservationValidator.PriceWarning(28_114, 28_104));
        Assert.NotNull(ObservationValidator.PriceWarning(379, 8_379));
        Assert.Null(ObservationValidator.PriceWarning(379, null));
    }

    // Aslarite at Levski, as UEX had it when a report of 5,100 as a buy price was declined.
    [Fact]
    public void A_price_on_the_side_uex_does_not_have_gets_a_warning()
    {
        var uex = new UexPrice { IdTerminal = 778, IdCommodity = 193, PriceBuy = 0, PriceSell = 5_100 };
        Assert.Contains("only as something you sell", ObservationValidator.SideWarning(Seen(778, 193, 5_100, null, 1), uex));
        Assert.Null(ObservationValidator.SideWarning(Seen(778, 193, null, 5_100, 1), uex));
        // Nothing to compare with: a commodity UEX has not seen here, or one it has on both sides.
        Assert.Null(ObservationValidator.SideWarning(Seen(778, 193, 5_100, null, 1), null));
        Assert.Null(ObservationValidator.SideWarning(Seen(778, 193, 5_100, null, 1), new UexPrice { PriceBuy = 5_300, PriceSell = 5_100 }));
        Assert.Null(ObservationValidator.SideWarning(Seen(778, 193, null, null, 1), uex));
    }
}
