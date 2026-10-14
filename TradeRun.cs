using System.Globalization;
using System.Text.Json;

namespace FreightCopilot;

// One thing the player ticks on the tablet during a trade run. Stop, Kind and Item place it in the checklist
// the relay carries; steps that share an Alternative are two ways of doing the same thing.
public sealed record TradeStep(string Id, string Label, string ListLabel, int Stop, string Kind, int Item, string Alternative = "")
{
    public string Key => $"{Stop}|{Kind}|{Item}";
}

// A "what can I haul from here" run sent to the tablet: what to buy where, where to sell it, and the steps of
// flying it. The tablet ticks the steps; each tick's time is kept, and the finished run goes into the journal.
public sealed class TradeRun
{
    public const string Bought = "bought", Auto = "auto", Warehouse = "warehouse", Packed = "packed", Undocked = "undocked",
        Quantum = "quantum", Jump = "jump", Docked = "docked", Unloaded = "unloaded", Sold = "sold";
    private const string Loading = "loaded";

    private readonly Dictionary<string, DateTime> _done = [];

    public TradeRun(string ship, UexTerminal origin, int destinationId, string destination, string destinationSystem, int commodityId, string commodity,
        int scu, double priceBuy, double priceSell, string crates, double? distance, string source, string confidence, DateTime startedUtc,
        string destinationDetail = "")
    {
        (Ship, OriginId, Origin, OriginSystem, OriginDetail) = (ship, origin.Id, origin.Label, origin.StarSystemName ?? "", origin.FullName);
        (DestinationId, Destination, DestinationSystem, DestinationDetail) = (destinationId, destination, destinationSystem, destinationDetail);
        (CommodityId, Commodity, Scu, PriceBuy, PriceSell) = (commodityId, commodity, scu, priceBuy, priceSell);
        (Crates, Distance, Source, Confidence, StartedUtc) = (crates, distance, source, confidence, startedUtc);

        var steps = new List<TradeStep>
        {
            new(Bought, "Bought commodities", $"Bought {Whole(scu)} SCU {commodity} at {Whole(priceBuy)}/SCU ({crates})", 0, "P", 0),
            // The terminal loads the ship for a fee, or the cargo is fetched from the warehouse.
            new(Auto, "Auto loaded", "Auto loaded", 0, "P", 1, Loading),
            new(Warehouse, "Warehouse pickup", "Warehouse pickup", 0, "P", 2, Loading),
            new(Packed, "Packed", "Packed", 0, "K", 0),
            new(Undocked, "Undocked / left planetary pad", "Undocked / left planetary pad", 0, "K", 1),
            new(Quantum, "Quantum travel done", "Quantum travel done", 1, "K", 0)
        };
        // Another star system is reached through a jump point; inside one system there is nothing to tick.
        if (OriginSystem.Length > 0 && DestinationSystem.Length > 0 && !OriginSystem.Equals(DestinationSystem, StringComparison.OrdinalIgnoreCase))
            steps.Add(new(Jump, $"Jumped to {DestinationSystem}", $"Jumped to {DestinationSystem}", 1, "K", 1));
        steps.Add(new(Docked, "Docked", "Docked", 1, "K", steps.Count(x => x is { Stop: 1, Kind: "K" })));
        steps.Add(new(Unloaded, "Unloaded", "Unloaded", 1, "D", 0));
        steps.Add(new(Sold, "Sold", $"Sold {Whole(scu)} SCU {commodity} at {Whole(priceSell)}/SCU", 1, "D", 1));
        Steps = steps;
    }

    public string Ship { get; }
    public int OriginId { get; }
    public string Origin { get; }
    public string OriginSystem { get; }
    // Which office the terminal is in and where in the city, where UEX's full name says so. A city has several
    // commodity terminals, and only one of them takes the cargo.
    public string OriginDetail { get; }
    public int DestinationId { get; }
    public string Destination { get; }
    public string DestinationSystem { get; }
    public string DestinationDetail { get; }
    public int CommodityId { get; }
    public string Commodity { get; }
    public int Scu { get; }
    public double PriceBuy { get; }
    public double PriceSell { get; }
    public string Crates { get; }
    public double? Distance { get; }
    public string Source { get; }
    public string Confidence { get; }
    // The clock starts when the run is sent to the tablet.
    public DateTime StartedUtc { get; }
    public IReadOnlyList<TradeStep> Steps { get; }
    public IReadOnlyDictionary<string, DateTime> Done => _done;
    // The first relay revision that carried this run; ticks from before it belong to whatever the tablet showed then.
    public long FirstRevision { get; set; }
    // The journal row this run is written to, once the cargo is bought.
    public long TradeId { get; set; }

    public double Profit => TradeMath.GrossProfit(Scu, PriceBuy, PriceSell);
    public bool IsCompleted => _done.ContainsKey(Sold);
    // When the cargo was on board: packed, or loaded when that box was left unticked.
    public DateTime? LoadedUtc => At(Packed) ?? At(Auto) ?? At(Warehouse);
    // Loaded or packed cargo was bought too, whether or not that box was ticked.
    public bool IsBought => _done.ContainsKey(Bought) || LoadedUtc is not null;
    public string Title => $"TRADE RUN // {Whole(Scu)} SCU {Commodity} // {Origin} → {Destination}";

    // The steps in flying order, with the two ways of loading counted as the one that was used.
    private IEnumerable<TradeStep> Milestones => Steps.Where(x => x.Alternative.Length == 0 || _done.ContainsKey(x.Id) ||
                                                                  (x.Id == Auto && !Steps.Any(other => other.Alternative == x.Alternative && _done.ContainsKey(other.Id))));

    // Applies a tick from the tablet. Kind "S" is the old tablet app's "stop complete" box. True when anything changed.
    public bool Apply(int stop, string kind, int item, bool done, DateTime utc)
    {
        var targets = (kind != "S" ? Steps.Where(x => x.Kind == kind && x.Item == item) : done ? Milestones : Steps).Where(x => x.Stop == stop).ToList();
        var changed = false;
        foreach (var step in targets)
        {
            if (!done) changed |= _done.Remove(step.Id);
            else if (!_done.ContainsKey(step.Id))
            {
                // Cargo is loaded one way or the other, not both.
                foreach (var other in Steps.Where(x => x.Alternative.Length > 0 && x.Alternative == step.Alternative && x.Id != step.Id)) _done.Remove(other.Id);
                _done[step.Id] = utc;
                changed = true;
            }
        }
        return changed;
    }

    // Seconds each finished step took, measured from the step finished before it (the start of the run for the first).
    public IReadOnlyList<(TradeStep Step, DateTime Utc, int Seconds)> Timeline()
    {
        var timeline = new List<(TradeStep, DateTime, int)>();
        var previous = StartedUtc;
        foreach (var step in Milestones)
        {
            if (!_done.TryGetValue(step.Id, out var utc)) continue;
            timeline.Add((step, utc, (int)Math.Max(0, Math.Round((utc - previous).TotalSeconds))));
            previous = utc;
        }
        return timeline;
    }

    // Whole seconds between two moments of the run, or null when either is missing or they are out of order.
    private static int? Between(DateTime? from, DateTime? to) => from is { } start && to is { } end && end >= start ? (int)Math.Round((end - start).TotalSeconds) : null;

    private DateTime? At(string id) => _done.TryGetValue(id, out var utc) ? utc : null;

    public int? TotalSeconds => Between(StartedUtc, At(Sold));

    // The journal's three stretches: until the cargo is on board, until the ship is docked again, until the cargo is sold.
    public (int? Loading, int? Travel, int? Unloading) Stretches() => (Between(StartedUtc, LoadedUtc), Between(LoadedUtc, At(Docked)), Between(At(Docked), At(Sold)));

    // Writes the run into a journal row. Prices are set once, so a correction made in the journal is not undone by the next tick.
    public void Fill(TradeRecord record)
    {
        if (record.Id == 0)
        {
            (record.Ship, record.OriginId, record.OriginName) = (Ship, OriginId, Origin);
            (record.DestinationId, record.DestinationName) = (DestinationId, Destination);
            (record.CommodityId, record.CommodityName, record.Scu, record.PriceBuy) = (CommodityId, Commodity, Scu, PriceBuy);
            record.BoughtUtc = At(Bought) ?? StartedUtc;
            record.Notes = $"Flown with the tablet checklist. The prices are the planned ones ({Source}); correct them if you paid or received something else.";
        }
        if (!IsCompleted) (record.PriceSell, record.SoldUtc) = (null, null);
        else (record.PriceSell, record.SoldUtc) = (record.PriceSell ?? PriceSell, At(Sold));
        (record.LoadingSeconds, record.TravelSeconds, record.UnloadingSeconds) = Stretches();
        record.StepTimes = JsonSerializer.Serialize(Timeline().Select(x => new StepTime(x.Step.Label, x.Utc, x.Seconds)));
    }

    // The run as the relay carries it. The relay knows routes only, so the stops hold the steps as checklist items
    // (which an older tablet app can still tick) and the summary holds the run itself as JSON for the trade screen.
    public MobilePlanSnapshot ToSnapshot(long revision, DateTime nowUtc)
    {
        var summary = JsonSerializer.Serialize(new
        {
            trade = 1,
            run = StartedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            ship = Ship, commodity = Commodity, scu = Scu, crates = Crates, buy = PriceBuy, sell = PriceSell,
            from = Place(Origin, OriginSystem), to = Place(Destination, DestinationSystem), fromDetail = OriginDetail, toDetail = DestinationDetail,
            distance = Distance, source = Source, confidence = Confidence,
            steps = Steps.Select(x => new { id = x.Id, key = x.Key, label = x.Label, alt = x.Alternative })
        });
        var underway = _done.ContainsKey(Undocked);
        return new MobilePlanSnapshot(revision, nowUtc, Title, summary, underway ? 1 : 0, IsCompleted,
        [
            new MobilePlanStop(1, Place(Origin, OriginSystem), 0, [], Items(0, "P"), Items(0, "K")),
            new MobilePlanStop(2, Place(Destination, DestinationSystem), 0, Items(1, "D"), [], Items(1, "K"))
        ], _done.ToDictionary(x => x.Key, x => new DateTimeOffset(x.Value.ToUniversalTime()).ToUnixTimeMilliseconds()));

        List<string> Items(int stop, string kind) => Steps.Where(x => x.Stop == stop && x.Kind == kind).OrderBy(x => x.Item).Select(x => x.ListLabel).ToList();
    }

    // One line for the status bar.
    public string Describe()
    {
        var steps = Milestones.ToList();
        var last = Timeline().LastOrDefault();
        var progress = $"{steps.Count(x => _done.ContainsKey(x.Id))} of {steps.Count} steps";
        if (!IsCompleted) return last.Step is null ? $"Trade run on the tablet: {progress}" : $"Trade run: {last.Step.Label.ToLowerInvariant()} at {last.Utc.ToLocalTime():HH:mm:ss} — {progress}";
        var seconds = TotalSeconds ?? 0;
        return $"Trade run finished in {TradeUi.Duration(seconds)}: {Profit:N0} aUEC planned profit" +
               (TradeMath.ProfitPerHour(Profit, seconds) is { } rate ? $", {rate:N0} aUEC/h" : "") + ". It is in the trading journal.";
    }

    private static string Place(string name, string system) => system.Length > 0 ? $"{name} — {system}" : name;

    // The relay's text is shown on the tablet as it is, so it must not depend on this PC's number format.
    private static string Whole(double value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);
}

// One finished step as it is kept with the journal row.
public sealed record StepTime(string Label, DateTime Utc, int Seconds);
