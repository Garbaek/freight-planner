using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FreightCopilot;

// The player's own market history and trading journal, in market.db next to the app.
// Observations are only ever added: a newer price never overwrites an older one.
public sealed class MarketStore
{
    // One entry per schema version. Append new entries; never edit one that has shipped.
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE observations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            star_system TEXT NOT NULL,
            terminal_id INTEGER NOT NULL,
            terminal_name TEXT NOT NULL,
            commodity_id INTEGER NOT NULL,
            commodity_name TEXT NOT NULL,
            price_buy REAL,
            price_sell REAL,
            scu_stock INTEGER,
            scu_demand INTEGER,
            status INTEGER,
            container_sizes TEXT NOT NULL DEFAULT '',
            observed_utc TEXT NOT NULL,
            source TEXT NOT NULL,
            verified INTEGER NOT NULL,
            submission TEXT NOT NULL DEFAULT 'NotSubmitted',
            submission_detail TEXT NOT NULL DEFAULT '',
            submitted_utc TEXT,
            screenshot_path TEXT NOT NULL DEFAULT '',
            notes TEXT NOT NULL DEFAULT '');
        CREATE INDEX ix_observations_place ON observations (terminal_id, commodity_id, observed_utc);
        CREATE TABLE trades (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ship TEXT NOT NULL,
            origin_id INTEGER NOT NULL,
            origin_name TEXT NOT NULL,
            destination_id INTEGER NOT NULL,
            destination_name TEXT NOT NULL,
            commodity_id INTEGER NOT NULL,
            commodity_name TEXT NOT NULL,
            scu INTEGER NOT NULL,
            price_buy REAL NOT NULL,
            price_sell REAL,
            loading_seconds INTEGER,
            travel_seconds INTEGER,
            unloading_seconds INTEGER,
            operating_costs REAL,
            bought_utc TEXT NOT NULL,
            sold_utc TEXT,
            notes TEXT NOT NULL DEFAULT '');
        CREATE TABLE screenshots (
            path TEXT PRIMARY KEY COLLATE NOCASE,
            length INTEGER NOT NULL,
            last_write_ticks INTEGER NOT NULL,
            processed_utc TEXT NOT NULL,
            outcome TEXT NOT NULL);
        """,
        // 0.8: the steps of a run flown with the tablet checklist.
        "ALTER TABLE trades ADD COLUMN step_times TEXT NOT NULL DEFAULT '';",
        // 0.9: personal UEX trade-history synchronization, one remote row for each side of a run.
        """
        ALTER TABLE trades ADD COLUMN uex_buy_id INTEGER;
        ALTER TABLE trades ADD COLUMN uex_sell_id INTEGER;
        ALTER TABLE trades ADD COLUMN uex_buy_submission TEXT NOT NULL DEFAULT 'NotSubmitted';
        ALTER TABLE trades ADD COLUMN uex_sell_submission TEXT NOT NULL DEFAULT 'NotSubmitted';
        ALTER TABLE trades ADD COLUMN uex_buy_detail TEXT NOT NULL DEFAULT '';
        ALTER TABLE trades ADD COLUMN uex_sell_detail TEXT NOT NULL DEFAULT '';
        """,
        // Community tracker: a stable idempotency key plus the last server result.
        """
        ALTER TABLE trades ADD COLUMN tracker_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE trades ADD COLUMN tracker_submission TEXT NOT NULL DEFAULT 'NotSubmitted';
        ALTER TABLE trades ADD COLUMN tracker_detail TEXT NOT NULL DEFAULT '';
        """,
        // Actual cargo and partial sales. Older completed rows become one sale at their recorded destination.
        """
        ALTER TABLE trades ADD COLUMN planned_scu INTEGER;
        ALTER TABLE trades ADD COLUMN planned_price_buy REAL;
        ALTER TABLE trades ADD COLUMN planned_price_sell REAL;
        CREATE TABLE trade_sales (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            trade_id INTEGER NOT NULL,
            terminal_id INTEGER NOT NULL,
            terminal_name TEXT NOT NULL,
            scu INTEGER NOT NULL,
            price_per_scu REAL NOT NULL,
            sold_utc TEXT NOT NULL,
            uex_id INTEGER,
            uex_submission TEXT NOT NULL DEFAULT 'NotSubmitted',
            uex_detail TEXT NOT NULL DEFAULT '',
            FOREIGN KEY(trade_id) REFERENCES trades(id) ON DELETE CASCADE);
        CREATE INDEX ix_trade_sales_trade ON trade_sales (trade_id, sold_utc, id);
        UPDATE trades SET planned_scu = scu, planned_price_buy = price_buy, planned_price_sell = price_sell;
        INSERT INTO trade_sales (trade_id, terminal_id, terminal_name, scu, price_per_scu, sold_utc, uex_id, uex_submission, uex_detail)
            SELECT id, destination_id, destination_name, scu, price_sell, sold_utc, uex_sell_id, uex_sell_submission, uex_sell_detail
            FROM trades WHERE price_sell IS NOT NULL AND price_sell > 0 AND sold_utc IS NOT NULL;
        """,
        // Preserve the route's intended buyer separately from the last actual sale terminal.
        """
        ALTER TABLE trades ADD COLUMN planned_destination_id INTEGER;
        ALTER TABLE trades ADD COLUMN planned_destination_name TEXT NOT NULL DEFAULT '';
        UPDATE trades SET planned_destination_id = destination_id, planned_destination_name = destination_name;
        """
    ];

    private const string ObservationColumns =
        "id, star_system, terminal_id, terminal_name, commodity_id, commodity_name, price_buy, price_sell, scu_stock, scu_demand, status, " +
        "container_sizes, observed_utc, source, verified, submission, submission_detail, submitted_utc, screenshot_path, notes";
    private const string TradeColumns =
        "id, ship, origin_id, origin_name, destination_id, destination_name, commodity_id, commodity_name, scu, price_buy, price_sell, " +
        "loading_seconds, travel_seconds, unloading_seconds, operating_costs, bought_utc, sold_utc, notes, step_times, " +
        "uex_buy_id, uex_sell_id, uex_buy_submission, uex_sell_submission, uex_buy_detail, uex_sell_detail, " +
        "tracker_id, tracker_submission, tracker_detail, planned_scu, planned_price_buy, planned_price_sell, planned_destination_id, planned_destination_name";

    private readonly string _connection;

    public MarketStore(string appDirectory)
    {
        PathOnDisk = Path.Combine(appDirectory, "market.db");
        _connection = new SqliteConnectionStringBuilder { DataSource = PathOnDisk, Pooling = false }.ToString();
        Migrate();
    }

    public string PathOnDisk { get; }

    public void Add(IEnumerable<MarketObservation> observations)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        foreach (var row in observations)
        {
            // A UEX report is copied in once, however often the same report is seen again.
            if (row.Source == PriceSource.Uex && Convert.ToInt64(Command(db, transaction,
                    "SELECT COUNT(*) FROM observations WHERE source = 'Uex' AND terminal_id = $terminal AND commodity_id = $commodity AND observed_utc = $observed AND (price_buy IS NULL) = $sell",
                    ("$terminal", row.TerminalId), ("$commodity", row.CommodityId), ("$observed", Text(row.ObservedUtc)), ("$sell", !row.IsBuy)).ExecuteScalar()) > 0)
                continue;
            row.Id = Convert.ToInt64(Command(db, transaction,
                $"INSERT INTO observations ({ObservationColumns[4..]}) VALUES ($system, $terminal, $terminalName, $commodity, $commodityName, $buy, $sell, $stock, $demand, $status, " +
                "$sizes, $observed, $source, $verified, $submission, $detail, $submitted, $screenshot, $notes); SELECT last_insert_rowid()",
                ("$system", row.StarSystem), ("$terminal", row.TerminalId), ("$terminalName", row.TerminalName), ("$commodity", row.CommodityId),
                ("$commodityName", row.CommodityName), ("$buy", row.PriceBuy), ("$sell", row.PriceSell), ("$stock", row.StockScu), ("$demand", row.DemandScu),
                ("$status", row.Status), ("$sizes", row.ContainerSizes), ("$observed", Text(row.ObservedUtc)), ("$source", row.Source.ToString()),
                ("$verified", row.Verified), ("$submission", row.Submission.ToString()), ("$detail", row.SubmissionDetail),
                ("$submitted", row.SubmittedUtc is { } submitted ? Text(submitted) : null), ("$screenshot", row.ScreenshotPath), ("$notes", row.Notes)).ExecuteScalar());
        }
        transaction.Commit();
    }

    public List<MarketObservation> Observations(int? terminalId = null, int? commodityId = null, PriceSource? source = null, int limit = 5000)
    {
        using var db = Open();
        using var reader = Command(db, null,
            $"SELECT {ObservationColumns} FROM observations WHERE ($terminal IS NULL OR terminal_id = $terminal) AND ($commodity IS NULL OR commodity_id = $commodity) " +
            "AND ($source IS NULL OR source = $source) ORDER BY observed_utc DESC, id DESC LIMIT $limit",
            ("$terminal", terminalId), ("$commodity", commodityId), ("$source", source?.ToString()), ("$limit", limit)).ExecuteReader();
        var rows = new List<MarketObservation>();
        while (reader.Read())
            rows.Add(new MarketObservation
            {
                Id = reader.GetInt64(0), StarSystem = reader.GetString(1), TerminalId = reader.GetInt32(2), TerminalName = reader.GetString(3),
                CommodityId = reader.GetInt32(4), CommodityName = reader.GetString(5), PriceBuy = Number(reader, 6), PriceSell = Number(reader, 7),
                StockScu = Whole(reader, 8), DemandScu = Whole(reader, 9), Status = Whole(reader, 10), ContainerSizes = reader.GetString(11),
                ObservedUtc = Time(reader.GetString(12)), Source = Enum.Parse<PriceSource>(reader.GetString(13)), Verified = reader.GetBoolean(14),
                Submission = Enum.Parse<SubmissionState>(reader.GetString(15)), SubmissionDetail = reader.GetString(16),
                SubmittedUtc = reader.IsDBNull(17) ? null : Time(reader.GetString(17)), ScreenshotPath = reader.GetString(18), Notes = reader.GetString(19)
            });
        return rows;
    }

    public void SetSubmission(IEnumerable<long> ids, SubmissionState state, string detail, DateTime? submittedUtc)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        foreach (var id in ids)
            Command(db, transaction, "UPDATE observations SET submission = $state, submission_detail = $detail, submitted_utc = $submitted WHERE id = $id",
                ("$state", state.ToString()), ("$detail", detail), ("$submitted", submittedUtc is { } time ? Text(time) : null), ("$id", id)).ExecuteNonQuery();
        transaction.Commit();
    }

    public void SetVerified(long id, bool verified)
    {
        using var db = Open();
        Command(db, null, "UPDATE observations SET verified = $verified WHERE id = $id", ("$verified", verified), ("$id", id)).ExecuteNonQuery();
    }

    public void DeleteObservation(long id)
    {
        using var db = Open();
        Command(db, null, "DELETE FROM observations WHERE id = $id", ("$id", id)).ExecuteNonQuery();
    }

    // When this commodity at this terminal last went to UEX as a real report, for its five-minute duplicate rule.
    public DateTime? LastSubmission(int terminalId, int commodityId)
    {
        using var db = Open();
        var value = Command(db, null,
            "SELECT MAX(submitted_utc) FROM observations WHERE terminal_id = $terminal AND commodity_id = $commodity AND submission = 'Submitted'",
            ("$terminal", terminalId), ("$commodity", commodityId)).ExecuteScalar();
        return value is string text ? Time(text) : null;
    }

    public void Save(TradeRecord trade)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        if (trade.TrackerId.Length == 0) trade.TrackerId = Guid.NewGuid().ToString("D");
        Normalize(trade);
        var values = new (string, object?)[]
        {
            ("$id", trade.Id), ("$ship", trade.Ship), ("$origin", trade.OriginId), ("$originName", trade.OriginName), ("$destination", trade.DestinationId),
            ("$destinationName", trade.DestinationName), ("$commodity", trade.CommodityId), ("$commodityName", trade.CommodityName), ("$scu", trade.Scu),
            ("$buy", trade.PriceBuy), ("$sell", trade.PriceSell), ("$loading", trade.LoadingSeconds), ("$travel", trade.TravelSeconds),
            ("$unloading", trade.UnloadingSeconds), ("$costs", trade.OperatingCosts), ("$bought", Text(trade.BoughtUtc)),
            ("$sold", trade.SoldUtc is { } sold ? Text(sold) : null), ("$notes", trade.Notes), ("$steps", trade.StepTimes),
            ("$uexBuyId", trade.UexBuyId), ("$uexSellId", trade.UexSellId),
            ("$uexBuySubmission", trade.UexBuySubmission.ToString()), ("$uexSellSubmission", trade.UexSellSubmission.ToString()),
            ("$uexBuyDetail", trade.UexBuyDetail), ("$uexSellDetail", trade.UexSellDetail), ("$trackerId", trade.TrackerId),
            ("$trackerSubmission", trade.TrackerSubmission.ToString()), ("$trackerDetail", trade.TrackerDetail),
            ("$plannedScu", trade.PlannedScu), ("$plannedBuy", trade.PlannedPriceBuy), ("$plannedSell", trade.PlannedPriceSell),
            ("$plannedDestination", trade.PlannedDestinationId), ("$plannedDestinationName", trade.PlannedDestinationName)
        };
        if (trade.Id > 0)
            Command(db, transaction,
                "UPDATE trades SET ship = $ship, origin_id = $origin, origin_name = $originName, destination_id = $destination, destination_name = $destinationName, " +
                "commodity_id = $commodity, commodity_name = $commodityName, scu = $scu, price_buy = $buy, price_sell = $sell, loading_seconds = $loading, " +
                "travel_seconds = $travel, unloading_seconds = $unloading, operating_costs = $costs, bought_utc = $bought, sold_utc = $sold, notes = $notes, step_times = $steps, " +
                "uex_buy_id = $uexBuyId, uex_sell_id = $uexSellId, uex_buy_submission = $uexBuySubmission, uex_sell_submission = $uexSellSubmission, " +
                "uex_buy_detail = $uexBuyDetail, uex_sell_detail = $uexSellDetail, tracker_id = $trackerId, " +
                "tracker_submission = $trackerSubmission, tracker_detail = $trackerDetail, planned_scu = $plannedScu, " +
                "planned_price_buy = $plannedBuy, planned_price_sell = $plannedSell, planned_destination_id = $plannedDestination, " +
                "planned_destination_name = $plannedDestinationName WHERE id = $id",
                values).ExecuteNonQuery();
        else
            trade.Id = Convert.ToInt64(Command(db, transaction,
                $"INSERT INTO trades ({TradeColumns[4..]}) VALUES ($ship, $origin, $originName, $destination, $destinationName, $commodity, $commodityName, $scu, $buy, $sell, " +
                "$loading, $travel, $unloading, $costs, $bought, $sold, $notes, $steps, $uexBuyId, $uexSellId, $uexBuySubmission, $uexSellSubmission, " +
                "$uexBuyDetail, $uexSellDetail, $trackerId, $trackerSubmission, $trackerDetail, $plannedScu, $plannedBuy, $plannedSell, " +
                "$plannedDestination, $plannedDestinationName); SELECT last_insert_rowid()", values).ExecuteScalar());

        // Keep sale IDs stable: UEX uses each ID as an independent retry/checkpoint key. The in-memory
        // list is still authoritative, so rows removed by an editor are deleted inside this transaction.
        var existingSaleIds = new List<long>();
        using (var reader = Command(db, transaction, "SELECT id FROM trade_sales WHERE trade_id = $id", ("$id", trade.Id)).ExecuteReader())
            while (reader.Read()) existingSaleIds.Add(reader.GetInt64(0));
        var retainedSaleIds = trade.Sales.Where(x => x.Id > 0).Select(x => x.Id).ToHashSet();
        foreach (var removedId in existingSaleIds.Where(x => !retainedSaleIds.Contains(x)))
            Command(db, transaction, "DELETE FROM trade_sales WHERE id = $id AND trade_id = $trade", ("$id", removedId), ("$trade", trade.Id)).ExecuteNonQuery();
        foreach (var sale in trade.Sales.OrderBy(x => x.SoldUtc).ThenBy(x => x.Id))
        {
            sale.TradeId = trade.Id;
            var saleValues = new (string, object?)[]
            {
                ("$id", sale.Id), ("$trade", trade.Id), ("$terminal", sale.TerminalId), ("$name", sale.TerminalName),
                ("$scu", sale.Scu), ("$price", sale.PricePerScu), ("$sold", Text(sale.SoldUtc)), ("$uexId", sale.UexId),
                ("$submission", sale.UexSubmission.ToString()), ("$detail", sale.UexDetail)
            };
            var updated = sale.Id > 0
                ? Command(db, transaction,
                    "UPDATE trade_sales SET terminal_id = $terminal, terminal_name = $name, scu = $scu, price_per_scu = $price, " +
                    "sold_utc = $sold, uex_id = $uexId, uex_submission = $submission, uex_detail = $detail WHERE id = $id AND trade_id = $trade",
                    saleValues).ExecuteNonQuery()
                : 0;
            if (updated == 0)
                sale.Id = Convert.ToInt64(Command(db, transaction,
                    "INSERT INTO trade_sales (trade_id, terminal_id, terminal_name, scu, price_per_scu, sold_utc, uex_id, uex_submission, uex_detail) " +
                    "VALUES ($trade, $terminal, $name, $scu, $price, $sold, $uexId, $submission, $detail); SELECT last_insert_rowid()",
                    saleValues).ExecuteScalar());
        }
        transaction.Commit();
    }

    public List<TradeRecord> Trades()
    {
        using var db = Open();
        var rows = new List<TradeRecord>();
        using (var reader = Command(db, null, $"SELECT {TradeColumns} FROM trades ORDER BY bought_utc DESC, id DESC").ExecuteReader())
            while (reader.Read())
                rows.Add(new TradeRecord
                {
                    Id = reader.GetInt64(0), Ship = reader.GetString(1), OriginId = reader.GetInt32(2), OriginName = reader.GetString(3),
                    DestinationId = reader.GetInt32(4), DestinationName = reader.GetString(5), CommodityId = reader.GetInt32(6), CommodityName = reader.GetString(7),
                    Scu = reader.GetInt32(8), PriceBuy = reader.GetDouble(9), PriceSell = Number(reader, 10), LoadingSeconds = Whole(reader, 11),
                    TravelSeconds = Whole(reader, 12), UnloadingSeconds = Whole(reader, 13), OperatingCosts = Number(reader, 14),
                    BoughtUtc = Time(reader.GetString(15)), SoldUtc = reader.IsDBNull(16) ? null : Time(reader.GetString(16)), Notes = reader.GetString(17),
                    StepTimes = reader.GetString(18), UexBuyId = reader.IsDBNull(19) ? null : reader.GetInt64(19),
                    UexSellId = reader.IsDBNull(20) ? null : reader.GetInt64(20), UexBuySubmission = Enum.Parse<SubmissionState>(reader.GetString(21)),
                    UexSellSubmission = Enum.Parse<SubmissionState>(reader.GetString(22)), UexBuyDetail = reader.GetString(23), UexSellDetail = reader.GetString(24),
                    TrackerId = reader.GetString(25), TrackerSubmission = Enum.Parse<SubmissionState>(reader.GetString(26)), TrackerDetail = reader.GetString(27),
                    PlannedScu = Whole(reader, 28) ?? reader.GetInt32(8), PlannedPriceBuy = Number(reader, 29), PlannedPriceSell = Number(reader, 30),
                    PlannedDestinationId = Whole(reader, 31) ?? reader.GetInt32(4), PlannedDestinationName = reader.GetString(32)
                });
        var byId = rows.ToDictionary(x => x.Id);
        using var sales = Command(db, null,
            "SELECT id, trade_id, terminal_id, terminal_name, scu, price_per_scu, sold_utc, uex_id, uex_submission, uex_detail " +
            "FROM trade_sales ORDER BY sold_utc, id").ExecuteReader();
        while (sales.Read() && byId.TryGetValue(sales.GetInt64(1), out var trade))
            trade.Sales.Add(new TradeSale
            {
                Id = sales.GetInt64(0), TradeId = sales.GetInt64(1), TerminalId = sales.GetInt32(2), TerminalName = sales.GetString(3),
                Scu = sales.GetInt32(4), PricePerScu = sales.GetDouble(5), SoldUtc = Time(sales.GetString(6)),
                UexId = sales.IsDBNull(7) ? null : sales.GetInt64(7), UexSubmission = Enum.Parse<SubmissionState>(sales.GetString(8)),
                UexDetail = sales.GetString(9)
            });
        return rows;
    }

    public TradeSale AddSale(long tradeId, int terminalId, string terminalName, int scu, double pricePerScu, DateTime soldUtc)
    {
        var trade = Trades().FirstOrDefault(x => x.Id == tradeId) ?? throw new InvalidOperationException("The journal run no longer exists.");
        if (scu <= 0 || scu > trade.RemainingScu) throw new InvalidOperationException($"The sale must be between 1 and {trade.RemainingScu:N0} SCU.");
        if (pricePerScu <= 0 || !double.IsFinite(pricePerScu)) throw new InvalidOperationException("The sale price must be greater than zero.");
        var sale = new TradeSale
        {
            TradeId = tradeId, TerminalId = terminalId, TerminalName = terminalName.Trim(), Scu = scu,
            PricePerScu = pricePerScu, SoldUtc = soldUtc.ToUniversalTime()
        };
        trade.Sales.Add(sale);
        Save(trade);
        return sale;
    }

    public void SetTradeSubmission(long tradeId, bool buy, SubmissionState state, string detail, long? uexTradeId)
    {
        using var db = Open();
        var side = buy ? "buy" : "sell";
        Command(db, null,
            $"UPDATE trades SET uex_{side}_submission = $state, uex_{side}_detail = $detail, uex_{side}_id = COALESCE($uexId, uex_{side}_id) WHERE id = $id",
            ("$state", state.ToString()), ("$detail", detail), ("$uexId", uexTradeId), ("$id", tradeId)).ExecuteNonQuery();
    }

    public void SetSaleSubmission(long saleId, SubmissionState state, string detail, long? uexTradeId)
    {
        using var db = Open();
        Command(db, null,
            "UPDATE trade_sales SET uex_submission = $state, uex_detail = $detail, uex_id = COALESCE($uexId, uex_id) WHERE id = $id",
            ("$state", state.ToString()), ("$detail", detail), ("$uexId", uexTradeId), ("$id", saleId)).ExecuteNonQuery();
    }

    public void SetTrackerSubmission(long tradeId, SubmissionState state, string detail)
    {
        using var db = Open();
        Command(db, null, "UPDATE trades SET tracker_submission = $state, tracker_detail = $detail WHERE id = $id",
            ("$state", state.ToString()), ("$detail", detail), ("$id", tradeId)).ExecuteNonQuery();
    }

    public void DeleteTrade(long id)
    {
        using var db = Open();
        Command(db, null, "DELETE FROM trade_sales WHERE trade_id = $id", ("$id", id)).ExecuteNonQuery();
        Command(db, null, "DELETE FROM trades WHERE id = $id", ("$id", id)).ExecuteNonQuery();
    }

    // True when this exact file (same size and write time) has been read before.
    public bool HasSeenScreenshot(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            using var db = Open();
            return Convert.ToInt64(Command(db, null, "SELECT COUNT(*) FROM screenshots WHERE path = $path AND length = $length AND last_write_ticks = $ticks",
                ("$path", path), ("$length", info.Length), ("$ticks", info.LastWriteTimeUtc.Ticks)).ExecuteScalar()) > 0;
        }
        catch (IOException) { return false; }
    }

    public void MarkScreenshot(string path, string outcome)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            using var db = Open();
            Command(db, null, "INSERT OR REPLACE INTO screenshots (path, length, last_write_ticks, processed_utc, outcome) VALUES ($path, $length, $ticks, $processed, $outcome)",
                ("$path", path), ("$length", info.Length), ("$ticks", info.LastWriteTimeUtc.Ticks), ("$processed", Text(DateTime.UtcNow)), ("$outcome", outcome)).ExecuteNonQuery();
        }
        catch (IOException) { }
    }

    private void Migrate()
    {
        using var db = Open();
        var version = Convert.ToInt32(Command(db, null, "PRAGMA user_version").ExecuteScalar());
        if (version > Migrations.Length)
            throw new InvalidOperationException("market.db was written by a newer Freight Planner. Update the app to open it.");
        for (var next = version; next < Migrations.Length; next++)
        {
            using var transaction = db.BeginTransaction();
            Command(db, transaction, Migrations[next]).ExecuteNonQuery();
            Command(db, transaction, $"PRAGMA user_version = {next + 1}").ExecuteNonQuery();
            transaction.Commit();
        }
        // Existing completed rows need a stable server id too, including databases opened by an
        // intermediate build that created the columns before this backfill was added.
        if (Migrations.Length >= 4)
            Command(db, null, "UPDATE trades SET tracker_id = lower(hex(randomblob(16))) WHERE tracker_id = ''").ExecuteNonQuery();
    }

    private static void Normalize(TradeRecord trade)
    {
        if (trade.PlannedDestinationId <= 0) trade.PlannedDestinationId = trade.DestinationId;
        if (trade.PlannedDestinationName.Length == 0) trade.PlannedDestinationName = trade.DestinationName;
        if (trade.PlannedScu <= 0) trade.PlannedScu = trade.Scu;
        trade.PlannedPriceBuy ??= trade.PriceBuy > 0 ? trade.PriceBuy : null;
        trade.PlannedPriceSell ??= trade.PriceSell;
        if (trade.Sales.Count == 0 && trade.PriceSell is > 0 && trade.SoldUtc is { } sold)
            trade.Sales.Add(new TradeSale
            {
                TerminalId = trade.DestinationId, TerminalName = trade.DestinationName, Scu = trade.Scu,
                PricePerScu = trade.PriceSell.Value, SoldUtc = sold, UexId = trade.UexSellId,
                UexSubmission = trade.UexSellSubmission, UexDetail = trade.UexSellDetail
            });
        if (trade.Sales.Count == 0) return;
        if (trade.Sales.Any(x => x.Scu <= 0 || x.PricePerScu <= 0 || !double.IsFinite(x.PricePerScu)))
            throw new InvalidOperationException("Every sale must have positive SCU and a finite price greater than zero.");
        var soldScu = trade.Sales.Sum(x => x.Scu);
        if (soldScu > trade.Scu)
            throw new InvalidOperationException($"Sale rows account for {soldScu:N0} SCU, more than the {trade.Scu:N0} SCU bought.");
        trade.PriceSell = soldScu > 0 ? trade.Sales.Sum(x => x.Revenue) / soldScu : null;
        var last = trade.Sales.OrderBy(x => x.SoldUtc).ThenBy(x => x.Id).Last();
        trade.DestinationId = last.TerminalId;
        trade.DestinationName = last.TerminalName;
        trade.SoldUtc = soldScu >= trade.Scu ? last.SoldUtc : null;
        // These legacy fields summarize only a one-sale run. Multi-sale UEX state lives on each sale row.
        if (trade.Sales.Count == 1)
        {
            trade.UexSellId = last.UexId;
            trade.UexSellSubmission = last.UexSubmission;
            trade.UexSellDetail = last.UexDetail;
        }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] values)
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string Text(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static DateTime Time(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static double? Number(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetDouble(column);
    private static int? Whole(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt32(column);
}
