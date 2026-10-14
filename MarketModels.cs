namespace FreightCopilot;

public enum PriceSource { Uex, Manual, Screenshot }

public enum SubmissionState { NotSubmitted, Test, Submitted, Failed }

public enum PriceMode { Combined, UexOnly, LocalOnly }

// One sighting of a commodity at a terminal: what the terminal charges or pays and what it shows as stock.
// How much the player bought or sold is not part of it; that is a TradeRecord.
public sealed class MarketObservation
{
    public long Id { get; set; }
    public string StarSystem { get; set; } = "";
    public int TerminalId { get; set; }
    public string TerminalName { get; set; } = "";
    public int CommodityId { get; set; }
    public string CommodityName { get; set; } = "";
    // What the player pays (PriceBuy) or receives (PriceSell) per SCU. A row holds one of the two, as at UEX.
    public double? PriceBuy { get; set; }
    public double? PriceSell { get; set; }
    // The shop quantity the terminal shows for this row.
    public int? StockScu { get; set; }
    // How much the terminal still takes, where the game shows it. Kept locally only.
    public int? DemandScu { get; set; }
    // UEX inventory status 1 (out of stock) to 7 (maximum).
    public int? Status { get; set; }
    public string ContainerSizes { get; set; } = "";
    public DateTime ObservedUtc { get; set; }
    public PriceSource Source { get; set; }
    public bool Verified { get; set; }
    public SubmissionState Submission { get; set; }
    public string SubmissionDetail { get; set; } = "";
    public DateTime? SubmittedUtc { get; set; }
    public string ScreenshotPath { get; set; } = "";
    public string Notes { get; set; } = "";

    public bool IsBuy => PriceBuy is > 0;
    public double? Price => IsBuy ? PriceBuy : PriceSell;
}

// One actual sale from a cargo lot. A run may need several of these before its cargo is gone.
public sealed class TradeSale
{
    public long Id { get; set; }
    public long TradeId { get; set; }
    public int TerminalId { get; set; }
    public string TerminalName { get; set; } = "";
    public int Scu { get; set; }
    public double PricePerScu { get; set; }
    public DateTime SoldUtc { get; set; }
    public long? UexId { get; set; }
    public SubmissionState UexSubmission { get; set; }
    public string UexDetail { get; set; } = "";

    public double Revenue => Scu * PricePerScu;
}

// One cargo lot the player actually bought and hauled. It is open until its sale rows account for
// every bought SCU; a partial sale leaves the remainder on board.
public sealed class TradeRecord
{
    public long Id { get; set; }
    public string Ship { get; set; } = "";
    public int OriginId { get; set; }
    public string OriginName { get; set; } = "";
    public int DestinationId { get; set; }
    public string DestinationName { get; set; } = "";
    public int CommodityId { get; set; }
    public string CommodityName { get; set; } = "";
    // The route suggestion, kept separately from what the terminal actually allowed the player to buy.
    public int PlannedScu { get; set; }
    public double? PlannedPriceBuy { get; set; }
    public double? PlannedPriceSell { get; set; }
    public int Scu { get; set; }
    public double PriceBuy { get; set; }
    // Weighted average of the sale rows. Kept as a column for older journal code and exports.
    public double? PriceSell { get; set; }
    public int? LoadingSeconds { get; set; }
    public int? TravelSeconds { get; set; }
    public int? UnloadingSeconds { get; set; }
    public double? OperatingCosts { get; set; }
    public DateTime BoughtUtc { get; set; }
    public DateTime? SoldUtc { get; set; }
    public string Notes { get; set; } = "";
    // The steps ticked on the tablet with their times, as JSON; empty for a run entered by hand.
    public string StepTimes { get; set; } = "";
    // UEX stores the purchase and sale as two personal trade-history entries. Keeping their IDs makes
    // a repeated tablet command or a reopened Windows monitor harmless instead of adding duplicates.
    public long? UexBuyId { get; set; }
    public long? UexSellId { get; set; }
    public SubmissionState UexBuySubmission { get; set; }
    public SubmissionState UexSellSubmission { get; set; }
    public string UexBuyDetail { get; set; } = "";
    public string UexSellDetail { get; set; } = "";
    // Stable across retries and edits; the Docker server uses it as this account's idempotency key.
    public string TrackerId { get; set; } = "";
    public SubmissionState TrackerSubmission { get; set; }
    public string TrackerDetail { get; set; } = "";
    public List<TradeSale> Sales { get; set; } = [];

    public int SoldScu => Sales.Count > 0 ? Sales.Sum(x => x.Scu) : PriceSell is > 0 && SoldUtc is not null ? Scu : 0;
    public int RemainingScu => Math.Max(0, Scu - SoldScu);
    public double SaleRevenue => Sales.Count > 0 ? Sales.Sum(x => x.Revenue) : SoldScu * (PriceSell ?? 0);
    public double? AverageSellPrice => SoldScu > 0 ? SaleRevenue / SoldScu : null;
    public bool IsCompleted => Scu > 0 && RemainingScu == 0 && SoldUtc is not null;
    public bool IsPartiallySold => SoldScu > 0 && !IsCompleted;
    public string Route => $"{OriginName} → {DestinationName}";
}

public sealed class TradeSettings
{
    public PriceMode PriceMode { get; set; } = PriceMode.Combined;
    // Own prices older than this no longer replace UEX prices.
    public int LocalPriceMaxAgeHours { get; set; } = 48;
    public bool WatchTerminalScreenshots { get; set; }
    // Sends is_production = 0: a test of the connection, not a report.
    public bool SubmitAsTest { get; set; } = true;
    // The Bought and Sold checkpoints are explicit user actions. When this is on, each one is also
    // added to the user's private UEX trade history with the price recorded for the run.
    public bool SyncTradesToUex { get; set; }
    // Upload completed runs to the user's Docker-hosted community tracker and show the account display name
    // in aggregate rankings. Off by default because this leaves the PC.
    public bool ShareTradeStats { get; set; }
    // Sends one aggregate-only launch count with platform and app version. No installation id or account.
    public bool ShareAnonymousUsage { get; set; }
}
