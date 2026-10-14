namespace FreightCopilot.Tests;

// The catalog under test is the ships.json that ships with the app.
public class ShipCatalogTests
{
    private static readonly IReadOnlyList<ShipProfile> Ships = ShipCatalog.Load(AppContext.BaseDirectory);

    private static ShipProfile Ship(string name) => Ships.Single(x => x.Name == name);

    [Fact]
    public void Every_grid_listed_adds_up_to_the_ships_capacity()
    {
        var gridded = Ships.Where(x => x.Grids is { Count: > 0 }).ToList();
        Assert.True(gridded.Count > 90, $"only {gridded.Count} ships have grids");
        Assert.All(gridded, ship => Assert.True(ship.Grids!.Sum(x => x.Scu) == ship.CapacityScu, ship.Name));
    }

    [Fact]
    public void A_count_stands_for_that_many_grids()
    {
        // The C1 Spirit: two lanes, each two wide, two high and eight long.
        Assert.Equal([new CargoGrid(2, 2, 8), new CargoGrid(2, 2, 8)], Ship("Crusader C1 Spirit").Grids);
        Assert.Equal([new CargoGrid(3, 2, 4)], Ship("Consolidated Outland Nomad").Grids);
        Assert.Equal(16, Ship("MISC Hull C").Grids!.Count);
    }

    [Fact]
    public void Every_ship_can_be_filled_to_the_last_cell_with_single_scu_crates()
    {
        // The Hull C takes 4,608 of them; the search must neither run out of stack nor of patience.
        foreach (var ship in Ships.Where(x => x.Grids is { Count: > 0 }))
            Assert.True(CratePlanner.Plan(ship.CapacityScu, [1], ship.Grids).Scu == ship.CapacityScu, ship.Name);
    }

    [Fact]
    public void A_grid_can_refuse_crates_its_shape_has_room_for()
    {
        // The Corsair's hold is nine cells long, yet takes nothing above 24 SCU.
        var corsair = Ship("Drake Corsair");
        Assert.Equal(24, corsair.Grids![0].MaxCrateScu);
        Assert.Equal(24, CratePlanner.LargestCrate(corsair.Grids[0]));
        Assert.Equal((0, true), CratePlanner.MostOf(32, corsair.Grids));
        Assert.Equal((2, true), CratePlanner.MostOf(24, corsair.Grids));
        // 72 SCU of room, 64 SCU of crates: the strip left over is one cell deep, and an 8 SCU crate is two.
        Assert.Equal("2 × 24 SCU + 1 × 16 SCU", CratePlanner.Plan(72, [8, 16, 24, 32], corsair.Grids).Crates);
        // Without the limit the same hold would take two 32s.
        Assert.Equal((2, true), CratePlanner.MostOf(32, [new CargoGrid(4, 2, 9)]));
    }

    [Fact]
    public void The_largest_crate_follows_the_shape_of_the_grid()
    {
        Assert.Equal(16, CratePlanner.LargestCrate(new CargoGrid(3, 2, 4)));
        Assert.Equal(32, CratePlanner.LargestCrate(new CargoGrid(2, 2, 8)));
        // One layer high: a 4 SCU crate lies flat, an 8 does not fit.
        Assert.Equal(4, CratePlanner.LargestCrate(new CargoGrid(2, 1, 4)));
        // One cell wide, two high: a 4 SCU crate stands on its side.
        Assert.Equal(4, CratePlanner.LargestCrate(new CargoGrid(1, 2, 3)));
        Assert.Equal(1, CratePlanner.LargestCrate(new CargoGrid(1, 1, 1)));
    }

    [Fact]
    public void Crates_are_spread_over_several_grids()
    {
        // 64 SCU in two lanes: a 32 SCU crate in each, or a 32 and two 16s, but no third 32.
        var spirit = Ship("Crusader C1 Spirit").Grids!;
        Assert.Equal((2, true), CratePlanner.MostOf(32, spirit));
        Assert.Equal("2 × 32 SCU", CratePlanner.Plan(64, [1, 2, 4, 8, 16, 24, 32], spirit).Crates);
        Assert.Equal("2 × 24 SCU + 2 × 8 SCU", CratePlanner.Plan(64, [8, 24], spirit).Crates);
        // The Zeus CL: 120 SCU in the hold and 4 SCU in each of two side grids one cell deep.
        var zeus = Ship("RSI Zeus Mk II CL").Grids!;
        Assert.Equal([120, 4, 4], zeus.Select(x => x.Scu));
        Assert.Equal(2, CratePlanner.MostOf(32, zeus).Count);
        Assert.Equal(128, CratePlanner.Plan(128, [1, 2, 4, 8, 16, 24, 32], zeus).Scu);
    }

    [Fact]
    public void A_ship_is_found_under_the_name_it_had_before_the_game_renamed_it()
    {
        Assert.Equal("RSI Constellation Mk IV Taurus", ShipCatalog.Find(Ships, "RSI Constellation Taurus")?.Name);
        Assert.Equal("RSI Aurora Mk I CL", ShipCatalog.Find(Ships, "RSI Aurora CL")?.Name);
        Assert.Equal("ARGO RAFT", ShipCatalog.Find(Ships, "ARGO RAFT")?.Name);
        Assert.Null(ShipCatalog.Find(Ships, "No such ship"));
    }

    [Fact]
    public void The_cargo_of_a_ship_is_described_grid_by_grid()
    {
        var text = ShipCatalog.DescribeCargo(Ship("Consolidated Outland Nomad"), "4.10.2");
        Assert.Contains("24 SCU on 1 cargo grid", text);
        Assert.Contains("3 wide × 2 high × 4 long = 24 SCU", text);
        // 24 SCU of room, yet only two 8 SCU crates: they are two cells wide and the bed is three.
        Assert.Contains("1 × 16 SCU = 16 SCU", text);
        Assert.Contains("2 × 8 SCU = 16 SCU", text);
        Assert.Contains("6 × 4 SCU = 24 SCU", text);
        Assert.Contains("No 32, 24 SCU crate fits.", text);
        Assert.DoesNotContain("At least", text);
        Assert.Contains("game files of patch 4.10.2", text);
        Assert.Equal("grid 3 × 2 × 4, crates up to 16 SCU", ShipCatalog.SummarizeCargo(Ship("Consolidated Outland Nomad")));

        var spirit = ShipCatalog.DescribeCargo(Ship("Crusader C1 Spirit"));
        Assert.Contains("64 SCU on 2 cargo grids", spirit);
        Assert.Contains("2 × 2 wide × 2 high × 8 long = 32 SCU each", spirit);
        Assert.Equal("2 grids, crates up to 32 SCU", ShipCatalog.SummarizeCargo(Ship("Crusader C1 Spirit")));
    }

    [Fact]
    public void A_count_that_is_found_but_not_proven_is_given_as_at_least()
    {
        // Nine 4 SCU crates are placed on 3 x 3 x 5 cells; whether a tenth fits is more than the search settles.
        Assert.Equal((9, false), CratePlanner.MostOf(4, [new CargoGrid(3, 3, 5)]));
        var text = ShipCatalog.DescribeCargo(new ShipProfile("Test hold", 45, [new CargoGrid(3, 3, 5)]));
        Assert.Contains("at least 9 × 4 SCU = 36 SCU", text);
        Assert.Contains("   1 × 16 SCU = 16 SCU", text);
        Assert.Contains("more could not be ruled out", text);
    }

    [Fact]
    public void A_ship_without_a_grid_says_so()
    {
        var custom = Ship("Custom");
        Assert.Contains("No cargo grid is listed", ShipCatalog.DescribeCargo(custom));
        Assert.Equal("no cargo grid listed", ShipCatalog.SummarizeCargo(custom));
        Assert.Contains("cargo-module configuration", ShipCatalog.DescribeCargo(Ship("Aegis Retaliator (cargo modules)")));
    }

    [Fact]
    public void The_file_names_the_patch_its_grids_are_from() => Assert.StartsWith("4.", ShipCatalog.VerifiedForPatch(AppContext.BaseDirectory));
}
