namespace FreightCopilot;

public static class CratePlanner
{
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
}
