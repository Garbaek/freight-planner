using System.Text.Json;

namespace FreightCopilot.Tests;

public class TradeRunTests
{
    private static readonly DateTime Start = new(2026, 10, 10, 11, 30, 0, DateTimeKind.Utc);
    private static readonly UexTerminal PssLambda = new() { Id = 843, Name = "Admin - People's Service Station Lambda", Nickname = "PSS Lambda", StarSystemName = "Nyx" };

    // The Compboard run from PSS Lambda to Levski in a Nomad.
    private static TradeRun Run(string destinationSystem = "Nyx") => new("Consolidated Outland Nomad", PssLambda, 778, "Levski", destinationSystem, 18, "Compboard",
        24, 28_113.75, 34_650, "12 × 2 SCU", 62, "Mine", "High", Start);

    private static DateTime After(int minutes, int seconds = 0) => Start.AddMinutes(minutes).AddSeconds(seconds);

    [Fact]
    public void A_run_inside_one_system_has_eight_steps_with_two_boxes_for_loading()
    {
        var run = Run();
        Assert.Equal(["bought", "auto", "warehouse", "packed", "undocked", "quantum", "docked", "unloaded", "sold"], run.Steps.Select(x => x.Id));
        Assert.Equal(["0|P|0", "0|P|1", "0|P|2", "0|K|0", "0|K|1", "1|K|0", "1|K|1", "1|D|0", "1|D|1"], run.Steps.Select(x => x.Key));
        Assert.Equal(["Bought commodities", "Auto loaded", "Warehouse pickup", "Packed", "Undocked / left planetary pad"], run.Steps.Take(5).Select(x => x.Label));
        Assert.Equal(["", "loaded", "loaded"], run.Steps.Take(3).Select(x => x.Alternative));
        Assert.All(run.Steps.Skip(3), x => Assert.Equal("", x.Alternative));
    }

    [Fact]
    public void A_run_to_another_system_adds_the_jump()
    {
        var run = Run("Stanton");
        Assert.Equal(["bought", "auto", "warehouse", "packed", "undocked", "quantum", "jump", "docked", "unloaded", "sold"], run.Steps.Select(x => x.Id));
        Assert.Equal("1|K|1", run.Steps.Single(x => x.Id == "jump").Key);
        Assert.Equal("1|K|2", run.Steps.Single(x => x.Id == "docked").Key);
        Assert.Equal("Jumped to Stanton", run.Steps.Single(x => x.Id == "jump").Label);
    }

    [Fact]
    public void The_relay_carries_the_steps_as_checklist_items_and_the_run_as_json()
    {
        var plan = Run().ToSnapshot(7, Start);

        Assert.Equal(7, plan.Revision);
        Assert.Equal("TRADE RUN // 24 SCU Compboard // PSS Lambda → Levski", plan.Title);
        Assert.Equal(0, plan.CurrentStopIndex);
        Assert.Empty(plan.CheckedAtUnixMs!);
        Assert.Equal(["PSS Lambda — Nyx", "Levski — Nyx"], plan.Stops.Select(x => x.Location));
        // What an older tablet app shows: readable, and tickable in the same places.
        Assert.Equal(["Bought 24 SCU Compboard at 28,113.75/SCU (12 × 2 SCU)", "Auto loaded", "Warehouse pickup"], plan.Stops[0].Pickups);
        Assert.Equal(["Packed", "Undocked / left planetary pad"], plan.Stops[0].Packing);
        Assert.Empty(plan.Stops[0].Deliveries);
        Assert.Equal(["Quantum travel done", "Docked"], plan.Stops[1].Packing);
        Assert.Equal(["Unloaded", "Sold 24 SCU Compboard at 34,650/SCU"], plan.Stops[1].Deliveries);

        using var json = JsonDocument.Parse(plan.Summary);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("trade").GetInt32());
        Assert.Equal("Compboard", root.GetProperty("commodity").GetString());
        Assert.Equal(24, root.GetProperty("scu").GetInt32());
        Assert.Equal(28_113.75, root.GetProperty("buy").GetDouble());
        Assert.Equal(34_650, root.GetProperty("sell").GetDouble());
        Assert.Equal("12 × 2 SCU", root.GetProperty("crates").GetString());
        Assert.Equal("PSS Lambda — Nyx", root.GetProperty("from").GetString());
        Assert.Equal("Levski — Nyx", root.GetProperty("to").GetString());
        Assert.Equal("Admin - People's Service Station Lambda", root.GetProperty("fromDetail").GetString());
        Assert.Equal("", root.GetProperty("toDetail").GetString());
        Assert.Equal(62, root.GetProperty("distance").GetDouble());
        Assert.Equal(["0|P|0", "0|P|1", "0|P|2", "0|K|0", "0|K|1", "1|K|0", "1|K|1", "1|D|0", "1|D|1"], root.GetProperty("steps").EnumerateArray().Select(x => x.GetProperty("key").GetString()));
        Assert.Equal("", root.GetProperty("steps")[0].GetProperty("alt").GetString());
        Assert.Equal("loaded", root.GetProperty("steps")[2].GetProperty("alt").GetString());
    }

    [Fact]
    public void The_tablet_is_told_which_office_in_the_city_takes_the_cargo()
    {
        // New Babbage has two commodity terminals in the Commons, and only the TDD buys gold.
        var tdd = new UexTerminal { Id = 89, Name = "TDD - Trade and Development Division - Commons - New Babbage", Nickname = "TDD New Babbage", Displayname = "New Babbage", StarSystemName = "Stanton" };
        Assert.Equal("TDD New Babbage", tdd.Label);
        Assert.Equal("TDD - Trade and Development Division - Commons - New Babbage", tdd.FullName);
        // An outpost has one terminal and one name.
        var perlman = new UexTerminal { Id = 36, Name = "HDMS-Perlman", Nickname = "HDMS-Perlman", StarSystemName = "Stanton" };
        Assert.Equal("", perlman.FullName);

        var run = new TradeRun("Consolidated Outland Nomad", perlman, tdd.Id, tdd.Label, "Stanton", 33, "Gold", 24, 28_928, 32_000, "12 × 2 SCU", 38, "Mine buy · UEX sell", "Low", Start, tdd.FullName);
        using var json = JsonDocument.Parse(run.ToSnapshot(1, Start).Summary);
        Assert.Equal("TDD New Babbage — Stanton", json.RootElement.GetProperty("to").GetString());
        Assert.Equal("TDD - Trade and Development Division - Commons - New Babbage", json.RootElement.GetProperty("toDetail").GetString());
        Assert.Equal("", json.RootElement.GetProperty("fromDetail").GetString());
    }

    [Fact]
    public void Two_sends_of_the_same_run_are_two_runs_for_the_tablet()
    {
        var first = Run().ToSnapshot(1, Start).Summary;
        var again = new TradeRun("Consolidated Outland Nomad", PssLambda, 778, "Levski", "Nyx", 18, "Compboard", 24, 28_113.75, 34_650, "12 × 2 SCU", 62, "Mine", "High",
            Start.AddHours(1)).ToSnapshot(2, Start.AddHours(1)).Summary;
        // Otherwise the tablet would show the second trip with the first trip's ticks.
        Assert.NotEqual(first, again);
    }

    [Fact]
    public void A_tick_is_kept_with_its_time_and_an_untick_forgets_it()
    {
        var run = Run();
        Assert.True(run.Apply(0, "P", 0, done: true, After(2, 12)));
        Assert.Equal(After(2, 12), run.Done["bought"]);
        // The same tick again changes nothing and keeps the first time.
        Assert.False(run.Apply(0, "P", 0, done: true, After(9)));
        Assert.Equal(After(2, 12), run.Done["bought"]);

        Assert.True(run.Apply(0, "P", 0, done: false, After(10)));
        Assert.Empty(run.Done);
        Assert.False(run.Apply(0, "P", 0, done: false, After(11)));
        // A box the run does not have.
        Assert.False(run.Apply(1, "K", 5, done: true, After(12)));
        Assert.False(run.Apply(3, "D", 0, done: true, After(12)));
    }

    [Fact]
    public void A_snapshot_carries_the_authoritative_checkpoint_times_to_both_monitors()
    {
        var run = Run();
        run.Apply(0, "P", 0, true, After(2, 12));
        run.Apply(0, "K", 0, true, After(3));

        var state = run.ToSnapshot(8, After(3)).CheckedAtUnixMs!;

        Assert.Equal(new DateTimeOffset(After(2, 12)).ToUnixTimeMilliseconds(), state[TradeRun.Bought]);
        Assert.Equal(new DateTimeOffset(After(3)).ToUnixTimeMilliseconds(), state[TradeRun.Packed]);
        Assert.Equal(2, state.Count);
    }

    [Fact]
    public void Cargo_is_loaded_one_way_or_the_other()
    {
        var run = Run();
        run.Apply(0, "P", 1, true, After(2));
        run.Apply(0, "P", 2, true, After(6));
        Assert.Equal(["warehouse"], run.Done.Keys);
        Assert.Equal(After(6), run.LoadedUtc);
        run.Apply(0, "P", 1, true, After(7));
        Assert.Equal(["auto"], run.Done.Keys);
    }

    [Fact]
    public void The_cargo_is_on_board_once_it_is_packed()
    {
        var run = Run();
        run.Apply(0, "P", 2, true, After(4));
        Assert.Equal(After(4), run.LoadedUtc);
        run.Apply(0, "K", 0, true, After(9));
        Assert.Equal(After(9), run.LoadedUtc);
    }

    [Fact]
    public void The_cargo_is_bought_from_its_first_tick_at_the_terminal()
    {
        var run = Run();
        Assert.False(run.IsBought);
        run.Apply(0, "P", 0, true, After(1));
        Assert.True(run.IsBought);

        // Packed without the first box ticked: the cargo was bought all the same.
        var packed = Run();
        packed.Apply(0, "K", 0, true, After(3));
        Assert.True(packed.IsBought);

        // Leaving without cargo is not a purchase.
        var empty = Run();
        empty.Apply(0, "K", 1, true, After(3));
        Assert.False(empty.IsBought);
    }

    private static TradeRun Flown(string destinationSystem = "Nyx")
    {
        var run = Run(destinationSystem);
        run.Apply(0, "P", 0, true, After(1, 5));     // bought commodities
        run.Apply(0, "P", 1, true, After(2, 12));    // auto loaded
        run.Apply(0, "K", 0, true, After(3));        // packed
        run.Apply(0, "K", 1, true, After(5));        // undocked
        run.Apply(1, "K", 0, true, After(21));       // quantum travel done
        run.Apply(1, "K", 1, true, After(25));       // docked
        run.Apply(1, "D", 0, true, After(28));       // unloaded
        run.Apply(1, "D", 1, true, After(29, 31));   // sold
        return run;
    }

    [Fact]
    public void Each_step_is_timed_from_the_one_before_it()
    {
        var timeline = Flown().Timeline();
        Assert.Equal(["Bought commodities", "Auto loaded", "Packed", "Undocked / left planetary pad", "Quantum travel done", "Docked", "Unloaded", "Sold"], timeline.Select(x => x.Step.Label));
        Assert.Equal([65, 67, 48, 120, 960, 240, 180, 91], timeline.Select(x => x.Seconds));
        Assert.Equal(29 * 60 + 31, timeline.Sum(x => x.Seconds));
    }

    [Fact]
    public void A_skipped_step_gives_its_time_to_the_next_one()
    {
        var run = Run();
        run.Apply(0, "P", 2, true, After(4));
        run.Apply(1, "K", 1, true, After(24));   // docked, without ticking packed, undocked or quantum travel
        Assert.Equal([("Warehouse pickup", 240), ("Docked", 1_200)], run.Timeline().Select(x => (x.Step.Label, x.Seconds)));
    }

    [Fact]
    public void The_journal_gets_loading_travel_and_unloading_times()
    {
        var run = Flown();
        Assert.True(run.IsCompleted);
        // Loading runs until the cargo is packed.
        Assert.Equal((180, 22 * 60, 4 * 60 + 31), run.Stretches());
        Assert.Equal(29 * 60 + 31, run.TotalSeconds);
        Assert.Equal(24 * (34_650 - 28_113.75), run.Profit);
    }

    [Fact]
    public void An_unfinished_run_has_no_total_time()
    {
        var run = Run();
        run.Apply(0, "P", 0, true, After(1));
        Assert.Equal((null, null, null), run.Stretches());
        run.Apply(0, "P", 1, true, After(2));
        Assert.False(run.IsCompleted);
        Assert.Null(run.TotalSeconds);
        Assert.Equal((120, null, null), run.Stretches());
    }

    [Fact]
    public void The_journal_row_is_open_until_sold_and_keeps_a_corrected_price()
    {
        var run = Run();
        run.Apply(0, "P", 0, true, After(1));
        run.Apply(0, "P", 1, true, After(2));
        var record = new TradeRecord();
        run.Fill(record);

        Assert.False(record.IsCompleted);
        Assert.Equal(("Consolidated Outland Nomad", 843, "PSS Lambda", 778, "Levski", 18, "Compboard", 24), (record.Ship, record.OriginId, record.OriginName,
            record.DestinationId, record.DestinationName, record.CommodityId, record.CommodityName, record.Scu));
        Assert.Equal(28_113.75, record.PriceBuy);
        Assert.Null(record.PriceSell);
        Assert.Equal(After(1), record.BoughtUtc);
        Assert.Equal(120, record.LoadingSeconds);
        Assert.Contains("planned", record.Notes);

        // Saved, then corrected in the journal: the terminal paid a little less than planned.
        record.Id = 5;
        record.PriceBuy = 28_200;
        record.Notes = "my own note";
        run.Apply(1, "K", 1, true, After(25));
        run.Apply(1, "D", 1, true, After(30));
        run.Fill(record);

        Assert.True(record.IsCompleted);
        Assert.Equal(28_200, record.PriceBuy);
        Assert.Equal(34_650, record.PriceSell);
        Assert.Equal("my own note", record.Notes);
        Assert.Equal(After(30), record.SoldUtc);
        Assert.Equal((120, 23 * 60, 5 * 60), (record.LoadingSeconds, record.TravelSeconds, record.UnloadingSeconds));
        var steps = JsonSerializer.Deserialize<List<StepTime>>(record.StepTimes)!;
        Assert.Equal(["Bought commodities", "Auto loaded", "Docked", "Sold"], steps.Select(x => x.Label));
        Assert.Equal([60, 60, 23 * 60, 5 * 60], steps.Select(x => x.Seconds));

        // Unticking "sold" reopens the row.
        run.Apply(1, "D", 1, false, After(31));
        run.Fill(record);
        Assert.False(record.IsCompleted);
        Assert.Null(record.SoldUtc);
    }

    [Fact]
    public void The_stop_marker_moves_on_once_undocked()
    {
        var run = Run();
        Assert.Equal(0, run.ToSnapshot(1, Start).CurrentStopIndex);
        run.Apply(0, "K", 0, true, After(4));   // packed: still at the terminal
        Assert.Equal(0, run.ToSnapshot(2, Start).CurrentStopIndex);
        run.Apply(0, "K", 1, true, After(5));
        Assert.Equal(1, run.ToSnapshot(3, Start).CurrentStopIndex);
    }

    [Fact]
    public void An_older_tablet_apps_stop_box_ticks_the_whole_stop()
    {
        var run = Run();
        run.Apply(0, "P", 2, true, After(4));
        Assert.True(run.Apply(0, "S", 0, true, After(6)));
        // The way of loading already chosen stays; the rest of the stop is ticked.
        Assert.Equal(["bought", "packed", "undocked", "warehouse"], run.Done.Keys.Order());

        Assert.True(run.Apply(0, "S", 0, false, After(7)));
        Assert.Empty(run.Done);

        Assert.True(run.Apply(0, "S", 0, true, After(8)));
        Assert.Equal(["auto", "bought", "packed", "undocked"], run.Done.Keys.Order());
    }

    [Fact]
    public void The_status_line_follows_the_run()
    {
        var run = Run();
        Assert.Contains("0 of 8 steps", run.Describe());
        run.Apply(0, "P", 1, true, After(2));
        // Loading counts once, whichever of its two boxes was ticked.
        Assert.Contains("1 of 8 steps", run.Describe());
        Assert.Contains("29:31", Flown().Describe());
        Assert.Contains("trading journal", Flown().Describe());
    }
}
