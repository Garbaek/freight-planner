namespace FreightCopilot;

// How far a suggested load is backed by the ship's real cargo grid.
public enum CrateBasis
{
    // Every crate was placed on the grid cells listed for the ship in ships.json.
    Grid,
    // ships.json has no grid for the ship: only its nominal SCU capacity was used, so the crates may not fit.
    Theoretical
}

public sealed record CrateLoad(int Scu, string Crates, CrateBasis Basis);

public static class CratePlanner
{
    // Crate dimensions in 1-SCU grid cells: two sides of the base and the height the crate normally stands at.
    private static readonly (int Scu, int A, int B, int C)[] Sizes =
    [
        (32, 2, 8, 2), (24, 2, 6, 2), (16, 2, 4, 2), (8, 2, 2, 2), (4, 2, 2, 1), (2, 1, 2, 1), (1, 1, 1, 1)
    ];

    // Every way to set a crate down. The first two keep it on its normal base; the rest turn it onto a side.
    private static readonly (int Width, int Length, int Height)[][] Turns = Sizes.Select(x => new[]
    {
        (x.A, x.B, x.C), (x.B, x.A, x.C), (x.A, x.C, x.B), (x.C, x.A, x.B), (x.B, x.C, x.A), (x.C, x.B, x.A)
    }.Distinct().ToArray()).ToArray();

    private const int MaxTurns = 6;

    private enum Fit { Yes, No, Undecided }

    // The same loads are asked about over and over: a plan is rebuilt for each preference and every time a timer
    // runs down, and many trade runs fill the ship to the brim with the same crate sizes. The answers are kept
    // per ship, for as long as its list of grids is.
    private sealed class Answers
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, int, int, int, int, int), Fit> Fits = new();
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(int Scu, int Sizes), CrateLoad> Loads = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<CargoGrid>, Answers> Known = new();

    // The standard crate sizes, largest first.
    public static IReadOnlyList<int> CrateSizes { get; } = Sizes.Select(x => x.Scu).ToArray();

    // The largest crate that can stand on the grid in any turn; 0 for a grid that takes none.
    public static int LargestCrate(CargoGrid grid)
    {
        for (var i = 0; i < Sizes.Length; i++)
        {
            if (grid.MaxCrateScu > 0 && Sizes[i].Scu > grid.MaxCrateScu) continue;
            foreach (var (width, length, height) in Turns[i])
                if (width <= grid.Width && length <= grid.Length && height <= grid.Height) return Sizes[i].Scu;
        }
        return 0;
    }

    // How many crates of one size the grids take with nothing else on board. Like every load, it is one a
    // placement was found for; Certain says that one more is ruled out, not merely that none was found.
    public static (int Count, bool Certain) MostOf(int crateScu, IReadOnlyList<CargoGrid> grids)
    {
        var size = Array.FindIndex(Sizes, x => x.Scu == crateScu);
        if (size < 0 || grids.Count == 0) return (0, true);
        var crates = new int[Sizes.Length];
        crates[size] = Plan(grids.Sum(x => x.Scu), [crateScu], grids).Scu / crateScu + 1;
        return (crates[size] - 1, Search(crates, grids, flatOnly: false, null) == Fit.No);
    }

    public static IReadOnlyList<int> Breakdown(int scu, int? maximumContainerScu)
    {
        if (scu <= 0) return [];
        var maximum = Math.Max(1, maximumContainerScu ?? 4);
        var crates = new List<int>();
        while (scu > 0)
        {
            var crate = Math.Min(maximum, scu);
            crates.Add(crate);
            scu -= crate;
        }
        return crates;
    }

    public static string Describe(int scu, int? maximumContainerScu) =>
        string.Join(" + ", Breakdown(scu, maximumContainerScu).Select(x => $"{x} SCU"));

    public static string DescribeContainers(IEnumerable<ContractScan> contracts) => Describe(CountContainers(contracts), new int[Sizes.Length]);

    private static string Describe(int[] counts, int[] turned) => string.Join(" + ", Sizes.Select((size, i) => (size.Scu, Count: counts[i], Turned: turned[i]))
        .Where(x => x.Count > 0).Select(x => $"{x.Count} × {x.Scu} SCU" + (x.Turned == 0 ? ""
            : x.Turned < x.Count ? $" ({x.Turned} on {(x.Turned == 1 ? "its" : "their")} side)"
            : x.Count == 1 ? " on its side" : " on their side")));

    // Fills up to scu with the largest allowed crates that still sit on the grids together, and says how each
    // has to be turned. The load is less than scu when the allowed sizes cannot make up the rest, and a crate
    // is only added when a placement for the whole load was actually found. Without grids nothing can be placed,
    // so only the nominal capacity is used and the result is marked theoretical.
    public static CrateLoad Plan(int scu, IReadOnlyCollection<int> allowedSizes, IReadOnlyList<CargoGrid>? grids)
    {
        if (grids is null || grids.Count == 0) return new CrateLoad(scu, $"up to {allowedSizes.Max()} SCU each", CrateBasis.Theoretical);
        var allowed = Sizes.Select((size, i) => allowedSizes.Contains(size.Scu) ? 1 << i : 0).Sum();
        return Known.GetOrCreateValue(grids).Loads.GetOrAdd((scu, allowed), _ => Load(scu, allowed, grids));
    }

    private static CrateLoad Load(int scu, int allowed, IReadOnlyList<CargoGrid> grids)
    {
        var counts = new int[Sizes.Length];
        var loaded = 0;
        for (var i = 0; i < Sizes.Length; i++)
        {
            if ((allowed & 1 << i) == 0) continue;
            // As many as there is room for by volume, when they fit; otherwise the most that do, by halving.
            var most = (scu - loaded) / Sizes[i].Scu;
            var fitting = 0;
            for (var tooMany = most + 1; fitting + 1 < tooMany;)
            {
                var attempt = fitting == 0 && tooMany == most + 1 ? most : (fitting + tooMany) / 2;
                counts[i] = attempt;
                if (Search(counts, grids, flatOnly: false, null) == Fit.Yes) fitting = attempt;
                else tooMany = attempt;
            }
            counts[i] = fitting;
            loaded += fitting * Sizes[i].Scu;
        }
        // Prefer an arrangement that leaves every crate on its normal base; turn crates only when that is the only way.
        var turned = new int[Sizes.Length];
        if (Search(counts, grids, flatOnly: true, turned) != Fit.Yes) Search(counts, grids, flatOnly: false, turned);
        return new CrateLoad(loaded, Describe(counts, turned), CrateBasis.Grid);
    }

    // True when every crate of the contracts can sit on the ship's cargo grids at the same time.
    // A load the search could not decide within its budget is not blocked.
    public static bool Fits(IEnumerable<ContractScan> contracts, IReadOnlyList<CargoGrid>? grids)
    {
        if (grids is null || grids.Count == 0) return true;
        var crates = CountContainers(contracts);
        var answers = Known.GetOrCreateValue(grids).Fits;
        if (answers.Count > 100_000) answers.Clear();
        return answers.GetOrAdd((crates[0], crates[1], crates[2], crates[3], crates[4], crates[5], crates[6]),
            _ => Search(crates, grids, flatOnly: false, null)) != Fit.No;
    }

    // Looks for a placement of all crates on the grid cells. A crate snaps to whole cells, may be turned onto any
    // side unless flatOnly, and must rest on the deck or on other crates over its whole base, so nothing floats
    // above a gap. On success, turned receives how many crates of each size ended up on a side.
    private static Fit Search(int[] crates, IReadOnlyList<CargoGrid> grids, bool flatOnly, int[]? turned)
    {
        const byte Free = 0, Crate = 1, LeftEmpty = 2;
        const int LeaveEmpty = 7 * MaxTurns;
        var counts = (int[])crates.Clone();
        var waiting = counts.Sum();
        var onSide = new int[Sizes.Length];
        // All grids share one array. A grid's cells follow each other layer by layer from the deck up, so the
        // order of the array is the order the cells are visited in.
        var starts = new int[grids.Count + 1];
        for (var g = 0; g < grids.Count; g++) starts[g + 1] = starts[g] + grids[g].Scu;
        var cells = new byte[starts[^1]];
        var spare = cells.Length - counts.Select((count, i) => count * Sizes[i].Scu).Sum();
        if (spare < 0 || !HasRoom(counts, grids)) return Fit.No;
        // Bounds the search: a load that neither fits at once nor is ruled out at once is left undecided.
        var budget = 20_000;

        // The first free cell is either the lowest corner of some crate or stays empty, so trying every crate
        // there, plus leaving it empty while spare cells remain, is exhaustive; and everything below a cell is
        // already settled when the cell is reached. The choices made so far are kept as a list instead of a call
        // chain, because a large hold takes thousands of crates.
        var cellOf = new int[64];
        var choiceOf = new int[64];
        var made = 0;
        var cell = 0;
        var from = 0;
        while (true)
        {
            if (from == 0)
            {
                if (waiting == 0)
                {
                    if (turned is not null) onSide.CopyTo(turned, 0);
                    return Fit.Yes;
                }
                if (--budget < 0) return Fit.Undecided;
                cell = Array.IndexOf(cells, Free, cell);
            }
            var choice = cell < 0 ? -1 : Choose(cell, from);
            if (choice >= 0)
            {
                if (made == cellOf.Length)
                {
                    Array.Resize(ref cellOf, made * 2);
                    Array.Resize(ref choiceOf, made * 2);
                }
                (cellOf[made], choiceOf[made]) = (cell, choice);
                made++;
                cell++;
                from = 0;
                continue;
            }
            // Nothing more to try here: take back the choice before it and go on with the one after that.
            if (made == 0) return Fit.No;
            made--;
            (cell, choice) = (cellOf[made], choiceOf[made]);
            Apply(cell, choice, undo: true);
            from = choice + 1;
        }

        // Makes the first choice at the cell that works, starting at number from: a crate size in one of its
        // turns, and last of all leaving the cell empty. Returns its number, or -1 when there is none.
        int Choose(int at, int from)
        {
            var (g, x, z, y) = Where(at);
            for (var choice = from; choice < LeaveEmpty; choice++)
            {
                var (i, turn) = (choice / MaxTurns, choice % MaxTurns);
                if (counts[i] == 0 || grids[g].MaxCrateScu > 0 && Sizes[i].Scu > grids[g].MaxCrateScu)
                {
                    choice += MaxTurns - turn - 1;
                    continue;
                }
                if (turn >= Turns[i].Length) continue;
                var (w, l, h) = Turns[i][turn];
                if (h != Sizes[i].C && flatOnly || !CanPlace(g, x, z, y, w, l, h)) continue;
                Apply(at, choice, undo: false);
                return choice;
            }
            if (spare == 0) return -1;
            Apply(at, LeaveEmpty, undo: false);
            return LeaveEmpty;
        }

        void Apply(int at, int choice, bool undo)
        {
            var step = undo ? -1 : 1;
            if (choice == LeaveEmpty)
            {
                cells[at] = undo ? Free : LeftEmpty;
                spare -= step;
                return;
            }
            var (i, turn) = (choice / MaxTurns, choice % MaxTurns);
            var (g, x, z, y) = Where(at);
            var (w, l, h) = Turns[i][turn];
            for (var a = 0; a < w; a++)
            for (var b = 0; b < l; b++)
            for (var c = 0; c < h; c++)
                cells[Index(g, x + a, z + b, y + c)] = undo ? Free : Crate;
            counts[i] -= step;
            waiting -= step;
            if (h != Sizes[i].C) onSide[i] += step;
        }

        bool CanPlace(int g, int x, int z, int y, int w, int l, int h)
        {
            if (x + w > grids[g].Width || z + l > grids[g].Length || y + h > grids[g].Height) return false;
            for (var a = 0; a < w; a++)
            for (var b = 0; b < l; b++)
            {
                if (y > 0 && cells[Index(g, x + a, z + b, y - 1)] != Crate) return false;
                for (var c = 0; c < h; c++)
                    if (cells[Index(g, x + a, z + b, y + c)] != Free) return false;
            }
            return true;
        }

        int Index(int g, int x, int z, int y) => starts[g] + (y * grids[g].Width + x) * grids[g].Length + z;

        (int Grid, int X, int Z, int Y) Where(int at)
        {
            var g = 0;
            while (at >= starts[g + 1]) g++;
            var inGrid = at - starts[g];
            return (g, inGrid / grids[g].Length % grids[g].Width, inGrid % grids[g].Length, inGrid / (grids[g].Length * grids[g].Width));
        }
    }

    // False when the crates cannot go on the grids however they are arranged, which the search itself is slow to
    // establish. A crate needs a grid that takes its size. And a crate of 8 SCU or more has even sides, so wherever
    // it stands, an eighth of its cells are odd-numbered across, up and along the grid; a grid has few of those
    // when a side is odd: none of the three cells across the Nomad's bed but the middle one.
    private static bool HasRoom(int[] counts, IReadOnlyList<CargoGrid> grids)
    {
        var wanted = 0L;
        var offered = 0L;
        for (var i = 0; i < Sizes.Length; i++)
        {
            if (counts[i] == 0) continue;
            var size = Sizes[i].Scu;
            var room = 0L;
            foreach (var grid in grids)
                if (LargestCrate(grid) >= size)
                    room += size >= 8 ? OddCells(grid) / (size / 8) : grid.Scu / size;
            if (counts[i] > room) return false;
            if (size >= 8) wanted += (long)counts[i] * (size / 8);
        }
        if (wanted == 0) return true;
        foreach (var grid in grids)
            if (LargestCrate(grid) >= 8) offered += OddCells(grid);
        return wanted <= offered;

        static int OddCells(CargoGrid grid) => (grid.Width / 2) * (grid.Height / 2) * (grid.Length / 2);
    }

    // Number of crates per entry of Sizes, using the largest real crate sizes the contract allows.
    private static int[] CountContainers(IEnumerable<ContractScan> contracts)
    {
        var counts = new int[Sizes.Length];
        foreach (var contract in contracts)
        {
            var maximum = Math.Max(1, contract.MaxContainerScu ?? 4);
            foreach (var leg in contract.Cargo)
            {
                var scu = leg.Scu;
                for (var i = 0; i < Sizes.Length; i++)
                {
                    if (Sizes[i].Scu > maximum) continue;
                    counts[i] += scu / Sizes[i].Scu;
                    scu %= Sizes[i].Scu;
                }
            }
        }
        return counts;
    }
}
