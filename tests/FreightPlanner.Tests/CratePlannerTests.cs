namespace FreightCopilot.Tests;

public class CratePlannerTests
{
    // The Nomad's bed as listed in ships.json: 3 cells wide, 4 long, 2 high. 24 SCU nominal.
    private static readonly CargoGrid[] Nomad = [new(3, 2, 4)];

    private static ContractScan Job(int scu, int maximumCrate) => new() { MaxContainerScu = maximumCrate, Cargo = [new CargoLeg { Scu = scu }] };

    [Fact]
    public void Nominal_capacity_is_not_enough_three_8_scu_crates_do_not_fit_a_24_scu_nomad()
    {
        // 24 SCU of cargo in a 24 SCU ship, yet a 2-wide crate cannot stand three abreast on a 3-wide bed.
        Assert.False(CratePlanner.Fits([Job(24, 8)], Nomad));
        Assert.True(CratePlanner.Fits([Job(16, 8)], Nomad));
    }

    [Fact]
    public void The_same_24_scu_fits_in_smaller_crates()
    {
        Assert.True(CratePlanner.Fits([Job(24, 4)], Nomad));
        Assert.True(CratePlanner.Fits([Job(24, 2)], Nomad));
        Assert.True(CratePlanner.Fits([Job(24, 1)], Nomad));
    }

    [Fact]
    public void More_cargo_than_grid_cells_never_fits() => Assert.False(CratePlanner.Fits([Job(25, 1)], Nomad));

    [Fact]
    public void Crates_longer_than_the_bed_do_not_fit()
    {
        // A 24 SCU crate is 6 cells long; the bed is 4.
        Assert.False(CratePlanner.Fits([Job(24, 24)], Nomad));
        Assert.False(CratePlanner.Fits([Job(32, 32)], [new CargoGrid(4, 2, 4)]));
        Assert.True(CratePlanner.Fits([Job(32, 32)], [new CargoGrid(2, 2, 8)]));
    }

    [Fact]
    public void Several_jobs_must_fit_together()
    {
        Assert.True(CratePlanner.Fits([Job(16, 16), Job(8, 4)], Nomad));
        // The 16 takes the 2-wide part; an 8 SCU crate is 2 wide and has only a 1-wide strip left.
        Assert.False(CratePlanner.Fits([Job(16, 16), Job(8, 8)], Nomad));
    }

    [Fact]
    public void A_ship_without_a_listed_grid_is_limited_by_capacity_alone()
    {
        Assert.True(CratePlanner.Fits([Job(24, 8)], null));
        Assert.True(CratePlanner.Fits([Job(24, 8)], []));
    }

    [Fact]
    public void A_load_is_built_from_the_largest_crates_that_sit_on_the_grid()
    {
        var load = CratePlanner.Plan(24, [1, 2, 4, 8, 16], Nomad);
        Assert.Equal(24, load.Scu);
        Assert.Equal(CrateBasis.Grid, load.Basis);
        // The 16 lies flat; the two 4s only fit the remaining 1-wide strip standing on a side.
        Assert.Equal("1 × 16 SCU + 2 × 4 SCU on their side", load.Crates);
    }

    [Fact]
    public void Crates_stay_on_their_base_when_that_is_possible()
    {
        Assert.Equal("12 × 2 SCU", CratePlanner.Plan(24, [1, 2], Nomad).Crates);
        Assert.Equal("2 × 8 SCU + 2 × 4 SCU on their side", CratePlanner.Plan(24, [4, 8], Nomad).Crates);
        Assert.Equal("1 × 4 SCU", CratePlanner.Plan(4, [4], Nomad).Crates);
    }

    [Fact]
    public void A_load_is_smaller_than_asked_when_the_crate_sizes_cannot_fill_the_grid()
    {
        var load = CratePlanner.Plan(24, [8], Nomad);
        Assert.Equal(16, load.Scu);
        Assert.Equal("2 × 8 SCU", load.Crates);
        Assert.Equal(CrateBasis.Grid, load.Basis);
    }

    [Fact]
    public void Stock_below_capacity_limits_the_load()
    {
        var load = CratePlanner.Plan(7, [1, 2, 4], Nomad);
        Assert.Equal(7, load.Scu);
        Assert.Equal("1 × 4 SCU + 1 × 2 SCU + 1 × 1 SCU", load.Crates);
    }

    [Fact]
    public void Without_a_grid_the_load_is_marked_theoretical()
    {
        var load = CratePlanner.Plan(192, [1, 2, 4, 8, 16, 24, 32], null);
        Assert.Equal(192, load.Scu);
        Assert.Equal(CrateBasis.Theoretical, load.Basis);
        Assert.Equal("up to 32 SCU each", load.Crates);
    }

    [Fact]
    public void A_crate_can_stand_on_end_with_another_on_top()
    {
        // One cell wide, one long, three high: a 2 SCU crate fits only standing on end, with a 1 SCU on top.
        var load = CratePlanner.Plan(3, [1, 2], [new CargoGrid(1, 3, 1)]);
        Assert.Equal(3, load.Scu);
        Assert.Equal("1 × 2 SCU on its side + 1 × 1 SCU", load.Crates);
        Assert.True(CratePlanner.Fits([Job(3, 2)], [new CargoGrid(1, 3, 1)]));
    }

    [Fact]
    public void A_load_no_placement_was_found_for_is_not_offered()
    {
        // 3 x 5 cells, 3 high: nine 4 SCU crates are placed. A tenth has room by volume (40 of 45 cells), and
        // settling whether it fits takes longer than the search is allowed, so the planner stops at what it could place.
        CargoGrid[] hold = [new(3, 3, 5)];
        var load = CratePlanner.Plan(45, [4], hold);
        Assert.Equal(36, load.Scu);
        Assert.StartsWith("9 × 4 SCU", load.Crates);
        Assert.Equal(CrateBasis.Grid, load.Basis);
        // Contract planning keeps its old rule: a load it could not decide is not blocked.
        Assert.True(CratePlanner.Fits([Job(40, 4)], hold));
    }

    [Fact]
    public void A_load_that_cannot_fit_is_ruled_out_without_a_search()
    {
        // 9 x 9 cells, 2 high: sixteen 8 SCU crates fit. A seventeenth has room by volume (136 of 162 cells), but
        // the crate is two cells each way and a row of nine has room for four of them.
        CargoGrid[] hold = [new(9, 2, 9)];
        Assert.Equal("16 × 8 SCU", CratePlanner.Plan(144, [8], hold).Crates);
        Assert.True(CratePlanner.Fits([Job(128, 8)], hold));
        Assert.False(CratePlanner.Fits([Job(136, 8)], hold));
        // Mixed sizes share that room: fifteen 8s and a 16 are one block too many.
        Assert.False(CratePlanner.Fits([Job(120, 8), Job(16, 16)], hold));
        // And a crate needs a grid that takes its size, however much room there is otherwise.
        Assert.False(CratePlanner.Fits([Job(32, 32), Job(4, 1)], [new CargoGrid(7, 2, 7)]));
    }

    [Fact]
    public void Crates_are_kept_off_a_grid_that_takes_only_smaller_ones()
    {
        // Room for an 8 SCU crate, but the grid takes nothing above 2 SCU.
        CargoGrid[] limited = [new(2, 2, 2, MaxCrateScu: 2)];
        Assert.False(CratePlanner.Fits([Job(8, 8)], limited));
        Assert.True(CratePlanner.Fits([Job(8, 2)], limited));
        Assert.Equal("4 × 2 SCU", CratePlanner.Plan(8, [1, 2, 4, 8], limited).Crates);
        // With a second grid the large crate goes there and the small ones stay.
        CargoGrid[] both = [new(2, 2, 2, MaxCrateScu: 2), new(2, 2, 2)];
        Assert.Equal("1 × 8 SCU + 4 × 2 SCU", CratePlanner.Plan(16, [1, 2, 4, 8], both).Crates);
        Assert.False(CratePlanner.Fits([Job(16, 8)], both));
    }

    [Fact]
    public void The_crate_breakdown_for_contracts_is_unchanged()
    {
        Assert.Equal("4 SCU + 3 SCU", CratePlanner.Describe(7, 4));
        Assert.Equal([4, 4, 4], CratePlanner.Breakdown(12, null));
        Assert.Equal("1 × 16 SCU + 1 × 8 SCU", CratePlanner.DescribeContainers([Job(24, 16)]));
    }
}
