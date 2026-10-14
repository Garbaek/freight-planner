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
        """
    ];

    private const string ObservationColumns =
        "id, star_system, terminal_id, terminal_name, commodity_id, commodity_name, price_buy, price_sell, scu_stock, scu_demand, status, " +
        "container_sizes, observed_utc, source, verified, submission, submission_detail, submitted_utc, screenshot_path, notes";
    private const string TradeColumns =
        "id, ship, origin_id, origin_name, destination_id, destination_name, commodity_id, commodity_name, scu, price_buy, price_sell, " +
        "loading_seconds, travel_seconds, unloading_seconds, operating_costs, bought_utc, sold_utc, notes, step_times, " +
        "uex_buy_id, uex_sell_id, uex_buy_submission, uex_sell_submission, uex_buy_detail, uex_sell_detail, " +
        "tracker_id, tracker_submission, tracker_detail";

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
        if (trade.TrackerId.Length == 0) trade.TrackerId = Guid.NewGuid().ToString("D");
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
            ("$trackerSubmission", trade.TrackerSubmission.ToString()), ("$trackerDetail", trade.TrackerDetail)
        };
        if (trade.Id > 0)
            Command(db, null,
                "UPDATE trades SET ship = $ship, origin_id = $origin, origin_name = $originName, destination_id = $destination, destination_name = $destinationName, " +
                "commodity_id = $commodity, commodity_name = $commodityName, scu = $scu, price_buy = $buy, price_sell = $sell, loading_seconds = $loading, " +
                "travel_seconds = $travel, unloading_seconds = $unloading, operating_costs = $costs, bought_utc = $bought, sold_utc = $sold, notes = $notes, step_times = $steps, " +
                "uex_buy_id = $uexBuyId, uex_sell_id = $uexSellId, uex_buy_submission = $uexBuySubmission, uex_sell_submission = $uexSellSubmission, " +
                "uex_buy_detail = $uexBuyDetail, uex_sell_detail = $uexSellDetail, tracker_id = $trackerId, " +
                "tracker_submission = $trackerSubmission, tracker_detail = $trackerDetail WHERE id = $id",
                values).ExecuteNonQuery();
        else
            trade.Id = Convert.ToInt64(Command(db, null,
                $"INSERT INTO trades ({TradeColumns[4..]}) VALUES ($ship, $origin, $originName, $destination, $destinationName, $commodity, $commodityName, $scu, $buy, $sell, " +
                "$loading, $travel, $unloading, $costs, $bought, $sold, $notes, $steps, $uexBuyId, $uexSellId, $uexBuySubmission, $uexSellSubmission, " +
                "$uexBuyDetail, $uexSellDetail, $trackerId, $trackerSubmission, $trackerDetail); SELECT last_insert_rowid()", values).ExecuteScalar());
    }

    public List<TradeRecord> Trades()
    {
        using var db = Open();
        using var reader = Command(db, null, $"SELECT {TradeColumns} FROM trades ORDER BY bought_utc DESC, id DESC").ExecuteReader();
        var rows = new List<TradeRecord>();
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
                TrackerId = reader.GetString(25), TrackerSubmission = Enum.Parse<SubmissionState>(reader.GetString(26)), TrackerDetail = reader.GetString(27)
            });
        return rows;
    }

    public void SetTradeSubmission(long tradeId, bool buy, SubmissionState state, string detail, long? uexTradeId)
    {
        using var db = Open();
        var side = buy ? "buy" : "sell";
        Command(db, null,
            $"UPDATE trades SET uex_{side}_submission = $state, uex_{side}_detail = $detail, uex_{side}_id = COALESCE($uexId, uex_{side}_id) WHERE id = $id",
            ("$state", state.ToString()), ("$detail", detail), ("$uexId", uexTradeId), ("$id", tradeId)).ExecuteNonQuery();
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
