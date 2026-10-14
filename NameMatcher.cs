namespace FreightCopilot;

// Matches names as OCR read them ("F)ROTA", "SHIP AMMUNITION - SIZE 1") to the names UEX uses.
public static class NameMatcher
{
    // The commodity the text most likely means, or null when nothing is close enough or two are equally close.
    public static UexCommodity? Commodity(string text, IEnumerable<UexCommodity> commodities) =>
        Best(text, commodities.Where(x => x.Name is { Length: > 0 }), x => [x.Name!], 0.8);

    // The terminal named by the inventory picker on the terminal screen, when it shows the station and not a ship.
    public static UexTerminal? Terminal(string text, IEnumerable<UexTerminal> terminals) =>
        Best(text, terminals, x => new[] { x.Label, x.Nickname, x.Displayname, x.Name }.Where(name => name is { Length: > 0 }).Select(name => name!), 0.9);

    // True when the text is some terminal's name, even where two terminals share it.
    public static bool IsAPlace(string text, IEnumerable<UexTerminal> terminals)
    {
        var wanted = Squash(text);
        return wanted.Length >= 3 && terminals.Any(x => new[] { x.Label, x.Nickname, x.Displayname, x.Name }.Any(name => name is { Length: > 0 } && Similarity(wanted, Squash(name)) >= 0.9));
    }

    private static T? Best<T>(string text, IEnumerable<T> items, Func<T, IEnumerable<string>> names, double minimum) where T : class
    {
        var wanted = Squash(text);
        if (wanted.Length < 3) return null;
        var ranked = items.Select(x => (Item: x, Score: names(x).Max(name => Similarity(wanted, Squash(name)))))
            .Where(x => x.Score >= minimum).OrderByDescending(x => x.Score).Take(2).ToList();
        return ranked.Count == 1 || ranked.Count == 2 && ranked[0].Score > ranked[1].Score ? ranked[0].Item : null;
    }

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    private static string Squash(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
}
