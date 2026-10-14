using Microsoft.Data.Sqlite;

namespace FreightCopilot.Tests;

public sealed class MarketStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("freight-planner-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static MarketObservation Compboard(double price, int minutesAgo, PriceSource source = PriceSource.Screenshot) => new()
    {
        StarSystem = "Nyx", TerminalId = 843, TerminalName = "PSS Lambda", CommodityId = 18, CommodityName = "Compboard",
        PriceBuy = price, StockScu = 51, Status = 5, ContainerSizes = "1,2", ObservedUtc = DateTime.UtcNow.AddMinutes(-minutesAgo),
        Source = source, Verified = source != PriceSource.Uex, ScreenshotPath = @"C:\shots\a.jpg", Notes = "seen in game"
    };

    [Fact]
    public void An_observation_comes_back_as_it_was_saved()
    {
        var store = new MarketStore(_directory);
        var saved = Compboard(28_114, 5);
        store.Add([saved]);

        var read = Assert.Single(new MarketStore(_directory).Observations());

        Assert.True(saved.Id > 0);
        Assert.Equal(saved.Id, read.Id);
        Assert.Equal("Nyx", read.StarSystem);
        Assert.Equal(843, read.TerminalId);
        Assert.Equal("Compboard", read.CommodityName);
        Assert.Equal(28_114, read.PriceBuy);
        Assert.Null(read.PriceSell);
        Assert.Equal(51, read.StockScu);
        Assert.Null(read.DemandScu);
        Assert.Equal(5, read.Status);
        Assert.Equal("1,2", read.ContainerSizes);
        Assert.Equal(saved.ObservedUtc, read.ObservedUtc);
        Assert.Equal(DateTimeKind.Utc, read.ObservedUtc.Kind);
        Assert.Equal(PriceSource.Screenshot, read.Source);
        Assert.True(read.Verified);
        Assert.Equal(SubmissionState.NotSubmitted, read.Submission);
        Assert.Equal(@"C:\shots\a.jpg", read.ScreenshotPath);
        Assert.Equal("seen in game", read.Notes);
    }

    [Fact]
    public void A_newer_price_is_added_beside_the_older_one()
    {
        var store = new MarketStore(_directory);
        store.Add([Compboard(28_000, 600)]);
        store.Add([Compboard(28_114, 5)]);

        Assert.Equal(new double[] { 28_114, 28_000 }, store.Observations(terminalId: 843, commodityId: 18).Select(x => x.PriceBuy!.Value));
    }

    [Fact]
    public void Observations_can_be_filtered()
    {
        var store = new MarketStore(_directory);
        var levski = Compboard(34_650, 5);
        levski.TerminalId = 778;
        levski.PriceBuy = null;
        levski.PriceSell = 34_650;
        store.Add([Compboard(28_114, 10), levski, Compboard(28_104, 9_000, PriceSource.Uex)]);

        Assert.Equal(3, store.Observations().Count);
        Assert.Equal(778, Assert.Single(store.Observations(terminalId: 778)).TerminalId);
        Assert.Equal(28_104, Assert.Single(store.Observations(source: PriceSource.Uex)).PriceBuy);
        Assert.Empty(store.Observations(commodityId: 1));
    }

    [Fact]
    public void The_same_uex_report_is_stored_once()
    {
        var store = new MarketStore(_directory);
        var report = Compboard(28_104, 9_000, PriceSource.Uex);
        store.Add([report]);
        store.Add([new MarketObservation
        {
            StarSystem = "Nyx", TerminalId = 843, TerminalName = "PSS Lambda", CommodityId = 18, CommodityName = "Compboard",
            PriceBuy = 28_104, ObservedUtc = report.ObservedUtc, Source = PriceSource.Uex
        }]);

        Assert.Single(store.Observations());
    }

    [Fact]
    public void Submission_and_confirmation_are_recorded()
    {
        var store = new MarketStore(_directory);
        var row = Compboard(28_114, 5);
        store.Add([row]);
        Assert.Null(store.LastSubmission(843, 18));

        var sent = DateTime.UtcNow;
        store.SetSubmission([row.Id], SubmissionState.Submitted, "report 123", sent);
        store.SetVerified(row.Id, false);

        var read = Assert.Single(store.Observations());
        Assert.Equal(SubmissionState.Submitted, read.Submission);
        Assert.Equal("report 123", read.SubmissionDetail);
        Assert.Equal(sent, read.SubmittedUtc);
        Assert.False(read.Verified);
        Assert.Equal(sent, store.LastSubmission(843, 18));
        Assert.Null(store.LastSubmission(778, 18));
    }

    [Fact]
    public void A_failed_submission_does_not_count_as_sent()
    {
        var store = new MarketStore(_directory);
        var row = Compboard(28_114, 5);
        store.Add([row]);
        store.SetSubmission([row.Id], SubmissionState.Failed, "UEX said no", DateTime.UtcNow);
        Assert.Null(store.LastSubmission(843, 18));
    }

    [Fact]
    public void An_observation_can_be_deleted()
    {
        var store = new MarketStore(_directory);
        var row = Compboard(28_114, 5);
        store.Add([row]);
        store.DeleteObservation(row.Id);
        Assert.Empty(store.Observations());
    }

    [Fact]
    public void A_trade_is_saved_completed_and_deleted()
    {
        var store = new MarketStore(_directory);
        var trade = new TradeRecord
        {
            Ship = "Consolidated Outland Nomad", OriginId = 843, OriginName = "PSS Lambda", DestinationId = 778, DestinationName = "Levski",
            CommodityId = 18, CommodityName = "Compboard", Scu = 6, PriceBuy = 28_113.75, OperatingCosts = 330, LoadingSeconds = 132,
            BoughtUtc = new DateTime(2026, 10, 10, 11, 34, 55, DateTimeKind.Utc)
        };
        store.Save(trade);
        Assert.False(Assert.Single(store.Trades()).IsCompleted);

        trade.PriceSell = 34_650;
        trade.UnloadingSeconds = 79;
        trade.SoldUtc = new DateTime(2026, 10, 10, 12, 8, 9, DateTimeKind.Utc);
        store.Save(trade);
        store.SetTradeSubmission(trade.Id, buy: true, SubmissionState.Submitted, "UEX trade 701", 701);
        store.SetTradeSubmission(trade.Id, buy: false, SubmissionState.Failed, "UEX unavailable", null);
        store.SetTrackerSubmission(trade.Id, SubmissionState.Submitted, "Stored on server");

        var read = Assert.Single(store.Trades());
        Assert.Equal(trade.Id, read.Id);
        Assert.True(read.IsCompleted);
        Assert.Equal(28_113.75, read.PriceBuy);
        Assert.Equal(34_650, read.PriceSell);
        Assert.Equal(330, read.OperatingCosts);
        Assert.Equal(132, read.LoadingSeconds);
        Assert.Null(read.TravelSeconds);
        Assert.Equal(79, read.UnloadingSeconds);
        Assert.Equal(trade.SoldUtc, read.SoldUtc);
        Assert.Equal(701, read.UexBuyId);
        Assert.Equal(SubmissionState.Submitted, read.UexBuySubmission);
        Assert.Equal("UEX trade 701", read.UexBuyDetail);
        Assert.Null(read.UexSellId);
        Assert.Equal(SubmissionState.Failed, read.UexSellSubmission);
        Assert.Equal("UEX unavailable", read.UexSellDetail);
        Assert.True(Guid.TryParse(read.TrackerId, out _));
        Assert.Equal(SubmissionState.Submitted, read.TrackerSubmission);
        Assert.Equal("Stored on server", read.TrackerDetail);

        store.DeleteTrade(trade.Id);
        Assert.Empty(store.Trades());
    }

    [Fact]
    public void A_cargo_lot_keeps_partial_sales_and_completes_on_the_last_sale()
    {
        var store = new MarketStore(_directory);
        var trade = new TradeRecord
        {
            Ship = "C2 Hercules", OriginId = 10, OriginName = "Buy", DestinationId = 20, DestinationName = "Planned buyer",
            CommodityId = 33, CommodityName = "Gold", PlannedScu = 100, PlannedPriceBuy = 100, PlannedPriceSell = 150,
            Scu = 80, PriceBuy = 110, BoughtUtc = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc)
        };
        store.Save(trade);

        var first = store.AddSale(trade.Id, 20, "Planned buyer", 30, 145, new DateTime(2026, 10, 13, 11, 0, 0, DateTimeKind.Utc));
        var partial = Assert.Single(store.Trades());
        Assert.True(partial.IsPartiallySold);
        Assert.Equal(30, partial.SoldScu);
        Assert.Equal(50, partial.RemainingScu);
        Assert.Null(partial.SoldUtc);

        store.SetSaleSubmission(first.Id, SubmissionState.Submitted, "UEX trade 901", 901);
        store.AddSale(trade.Id, 21, "Alternate buyer", 50, 140, new DateTime(2026, 10, 13, 11, 30, 0, DateTimeKind.Utc));
        var completed = Assert.Single(store.Trades());
        Assert.True(completed.IsCompleted);
        Assert.Equal(2, completed.Sales.Count);
        Assert.Equal(80, completed.SoldScu);
        Assert.Equal(0, completed.RemainingScu);
        Assert.Equal((30 * 145d + 50 * 140d) / 80d, completed.PriceSell);
        Assert.Equal(901, completed.Sales[0].UexId);

        store.Save(completed);
        Assert.Equal(completed.Sales.Select(x => x.Id), Assert.Single(store.Trades()).Sales.Select(x => x.Id));
    }

    [Fact]
    public void A_screenshot_is_remembered_until_the_file_changes()
    {
        var store = new MarketStore(_directory);
        var file = Path.Combine(_directory, "shot.jpg");
        File.WriteAllText(file, "first");
        Assert.False(store.HasSeenScreenshot(file));

        store.MarkScreenshot(file, "terminal");
        Assert.True(store.HasSeenScreenshot(file));
        Assert.True(store.HasSeenScreenshot(file.ToUpperInvariant()));

        File.WriteAllText(file, "a different picture");
        Assert.False(store.HasSeenScreenshot(file));
        Assert.False(store.HasSeenScreenshot(Path.Combine(_directory, "missing.jpg")));
    }

    [Fact]
    public void The_schema_version_is_recorded_and_a_newer_database_is_refused()
    {
        var store = new MarketStore(_directory);
        Assert.Equal(5L, Pragma(store.PathOnDisk, "PRAGMA user_version"));

        Pragma(store.PathOnDisk, "PRAGMA user_version = 99");
        Assert.Throws<InvalidOperationException>(() => new MarketStore(_directory));
    }

    [Fact]
    public void A_database_from_before_0_8_gains_the_step_times_and_keeps_its_runs()
    {
        var store = new MarketStore(_directory);
        var trade = new TradeRecord
        {
            Ship = "Consolidated Outland Nomad", OriginId = 843, OriginName = "PSS Lambda", DestinationId = 778, DestinationName = "Levski",
            CommodityId = 18, CommodityName = "Compboard", Scu = 6, PriceBuy = 28_113.75, PriceSell = 34_650,
            BoughtUtc = new DateTime(2026, 10, 10, 11, 34, 55, DateTimeKind.Utc), SoldUtc = new DateTime(2026, 10, 10, 12, 8, 9, DateTimeKind.Utc)
        };
        store.Save(trade);
        // Put the file back the way version 1 of the schema left it.
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN step_times");
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN uex_buy_id");
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN uex_sell_id");
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN uex_buy_submission");
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN uex_sell_submission");
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN uex_buy_detail");
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN uex_sell_detail");
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN tracker_id");
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN tracker_submission");
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN tracker_detail");
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN planned_scu");
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN planned_price_buy");
        Pragma(store.PathOnDisk, "ALTER TABLE trades DROP COLUMN planned_price_sell");
        Pragma(store.PathOnDisk, "DROP TABLE trade_sales");
        Pragma(store.PathOnDisk, "PRAGMA user_version = 1");

        var upgraded = new MarketStore(_directory);

        Assert.Equal(5L, Pragma(upgraded.PathOnDisk, "PRAGMA user_version"));
        var read = Assert.Single(upgraded.Trades());
        Assert.Equal("", read.StepTimes);
        Assert.Equal(34_650, read.PriceSell);
        Assert.True(Guid.TryParse(read.TrackerId, out _));
        read.StepTimes = "[{\"Label\":\"Sold\",\"Utc\":\"2026-10-10T12:08:09Z\",\"Seconds\":91}]";
        upgraded.Save(read);
        Assert.Contains("\"Seconds\":91", Assert.Single(upgraded.Trades()).StepTimes);
    }

    private static object? Pragma(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
