using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FreightCopilot;

public sealed class ScannedPrice
{
    public string Name { get; set; } = "";
    // True when the terminal sells it to the player, false when it buys it, null when the screen did not say.
    // Null is never turned into a guess: the player says which it is.
    public bool? IsBuy { get; set; }
    public double? Price { get; set; }
    // The shop quantity printed next to the price. The player's own cargo is never read into this.
    public int? ShopScu { get; set; }
    public int? Status { get; set; }
    public string ContainerSizes { get; set; } = "";
    // Things the player should know about the row.
    public List<string> Notes { get; set; } = [];
    // Things that were guessed. A row with a doubt is never confirmed in bulk.
    public List<string> Doubts { get; set; } = [];
    // Where this row's crate-size boxes are, for a closer look at the pixels.
    [JsonIgnore] public Rectangle? SizeBand { get; set; }
    // How far that row of boxes drops per pixel to the right; the band is where the row is at its right end.
    [JsonIgnore] public double SizeSlope { get; set; }
    // Where this row's quantity and price are, for the closer looks that cross-check them.
    [JsonIgnore] public Rectangle? FigureBand { get; set; }
    // False for the player's own goods, whose quantity is not the terminal's.
    [JsonIgnore] public bool HasShopQuantity { get; set; }
    // The part of the screenshot the row was read from, to show next to it for checking.
    [JsonIgnore] public Rectangle? Region { get; set; }
}

// A purchase or sale the player confirmed. It is a journal entry, not a statement about the terminal's stock.
public sealed class ScannedTransaction
{
    public string Name { get; set; } = "";
    public bool? IsPurchase { get; set; }
    public double? UnitPrice { get; set; }
    public int? Quantity { get; set; }
    public double? Total { get; set; }
    public double? Fee { get; set; }
    public int? Seconds { get; set; }
    [JsonIgnore] public Rectangle? TotalBand { get; set; }
}

public sealed class TerminalScan
{
    public bool IsTerminal => Prices.Count > 0 || Transaction is not null;
    // The inventory picked on the terminal: the station's name, or the ship's when selling from its hold.
    public string LocationHint { get; set; } = "";
    public List<ScannedPrice> Prices { get; set; } = [];
    public ScannedTransaction? Transaction { get; set; }
    public List<string> Warnings { get; set; } = [];
    // The labels of the two shop tabs; the lit one tells whether the shop rows are buy prices.
    [JsonIgnore] public Rectangle? BuyTab { get; set; }
    [JsonIgnore] public Rectangle? ValueTab { get; set; }
}

// Understands a full-frame OCR of the commodity terminal as in Star Citizen 4.x, measured on 2560x1440 screenshots
// of two terminal styles: the player's goods and their sell prices on the left, the shop's list on the right
// (BUY: what it sells; LOCAL MARKET VALUE: what it pays), or a transaction confirmation on top of both.
// A shop row is found by its "N SCU" quantity, which every style prints; anything not found stays empty.
public static partial class TerminalScreenParser
{
    public const string MissingPrice = "The price could not be read: type it in, or remove the row if it is cut off.";

    private static readonly string[] Labels =
    [
        "SHOPINVENTORY", "YOURINVENTORIES", "SHOPQUANTITY", "CURRENTBALANCE", "CARGOCAPACITY", "INSTOCK", "INDEMAND", "NODEMAND", "CANNOTSELL",
        "SELLABLECARGO", "COMMODITIES", "LOCALMARKETVALUE", "SELECTSUBCATEGORY", "SELECTLOCATION", "BUY", "SELL", "MAX"
    ];
    // Headings inside the shop list. The first is on the BUY tab, the others on LOCAL MARKET VALUE.
    private static readonly string[] BuySections = ["INSTOCK"];
    private static readonly string[] SellSections = ["INDEMAND", "NODEMAND", "SELLABLECARGO"];

    private sealed record Line(string Text, double X, double Y, double Right, double Bottom)
    {
        public double CenterY => (Y + Bottom) / 2;
        public string Squashed { get; } = Squash(Text);
        // How far the line drops per pixel to the right. A terminal seen from the side is not level all over,
        // even on a picture the OCR engine has turned level.
        public double Slope { get; init; }
        public bool Has(params string[] parts) => parts.All(Squashed.Contains);
    }

    public static bool LooksLikeTerminal(string ocrText)
    {
        var text = Squash(ocrText);
        return text.Contains("SHOPINVENTORY") || text.Contains("YOURINVENTORIES") || text.Contains("TRANSACTIONDETAILS");
    }

    public static TerminalScan Parse(OcrFrame frame)
    {
        var k = frame.Height / 1440.0;
        var lines = Lines(frame.Words);
        var scan = new TerminalScan();
        var details = lines.FirstOrDefault(x => x.Has("TRANSACTION", "DETAILS"));
        if (details is not null && lines.Any(x => x.Squashed == "CONFIRMATION"))
        {
            // The dialog dims the panels behind it, so nothing else on the screen is trusted.
            ReadConfirmation(scan, lines, details, k);
            return scan;
        }

        var shopHeader = lines.FirstOrDefault(x => x.Has("SHOP", "INVENTORY"));
        var quantityLabels = lines.Where(x => x.Has("SHOP", "QUANT")).ToList();
        var shopLeft = shopHeader is not null ? shopHeader.X - 20 * k : quantityLabels.Count > 0 ? quantityLabels.Min(x => x.X) - 400 * k : frame.Width;
        var shop = lines.Where(x => x.X >= shopLeft).ToList();
        scan.BuyTab = shop.Where(x => x.Squashed == "BUY").Select(Box).FirstOrDefault();
        // "LOCAL HARKET VALUE" in one terminal's font: two of the three words are enough.
        scan.ValueTab = shop.Where(x => new[] { "LOCAL", "MARKET", "VALUE" }.Count(x.Squashed.Contains) >= 2).Select(Box).FirstOrDefault();

        // A name and its status share a left edge; section headings and the icons sit further left, and on a
        // terminal seen from the side a heading starts left of the panel's own title too.
        var edges = shop.Where(x => IsStatus(x) && x.Squashed != "STOCK").Select(x => x.X).Order().ToList();
        var nameLeft = edges.Count > 0 ? edges[edges.Count / 2] - 24 * k : shopLeft + 55 * k;
        var sections = lines.Where(x => x.X >= shopLeft - 100 * k && x.X < nameLeft && (BuySections.Contains(x.Squashed) || SellSections.Contains(x.Squashed))).ToList();
        // One tab is open at a time, so a heading anywhere in the list says what all of its rows are, those above
        // the first heading too. Headings of both kinds mean something was misread, and then they say nothing.
        var kinds = sections.Select(x => BuySections.Contains(x.Squashed)).Distinct().ToList();
        bool? listIsBuy = kinds.Count == 1 ? kinds[0] : null;

        var prices = shop.Where(x => PricePattern().IsMatch(Tidy(x.Text))).ToList();
        var rows = shop.Where(x => QuantityPattern().IsMatch(x.Text))
            .Select(x => (Quantity: (Line?)x, Price: prices.FirstOrDefault(p => p.Y > x.Y - 4 * k && p.Y < x.Y + 55 * k && p.Right > x.X - 60 * k), x.Y)).ToList();
        // A price whose quantity was not read still marks a row.
        rows.AddRange(prices.Where(x => rows.All(row => row.Price != x)).Select(x => (Quantity: (Line?)null, Price: (Line?)x, Y: x.Y - 30 * k)));
        rows = rows.OrderBy(x => x.Y).ToList();

        for (var i = 0; i < rows.Count; i++)
        {
            var (quantity, price, y) = rows[i];
            var figures = (quantity ?? price)!;
            var top = y - 26 * k;
            var bottom = i + 1 < rows.Count ? rows[i + 1].Y - 26 * k : y + 170 * k;
            var block = shop.Where(x => x.CenterY >= top && x.CenterY < bottom).ToList();
            var sizeLabel = block.Where(x => x.Y > y && (x.Has("CARGO", "SIZE") || x.Has("AVAILABLE", "CARGO"))).OrderBy(x => x.Y).FirstOrDefault();
            // Name and status start at the name edge; "STOCK" split off "OUT OF" is the furthest in that still belongs.
            var left = block.Where(x => x != quantity && x != price && x.X >= nameLeft && x.X < Math.Min(nameLeft + 144 * k, figures.X - 60 * k) && x.Y < (sizeLabel?.Y ?? bottom))
                .OrderBy(x => Math.Round(x.Y / (10 * k))).ThenBy(x => x.X).ToList();
            var name = CleanName(left.Where(x => !IsStatus(x) && !IsLabel(x)).Select(x => x.Text));
            if (name.Length == 0) continue;

            var section = sections.Where(x => x.Y < y).OrderBy(x => x.Y).LastOrDefault()?.Squashed;
            var row = new ScannedPrice
            {
                Name = name,
                HasShopQuantity = true,
                IsBuy = listIsBuy,
                Status = StatusCode(string.Concat(left.Where(IsStatus).Select(x => x.Squashed))),
                FigureBand = FigureBand(quantity, price, k)
            };
            if (section == "NODEMAND") row.Notes.Add("Listed under NO DEMAND.");
            row.Price = price is null ? null : Figure(PricePattern().Match(Tidy(price.Text)), "price", row.Doubts);
            row.ShopScu = quantity is null ? null : (int?)Figure(QuantityPattern().Match(quantity.Text), "shop quantity", row.Doubts);
            if (row.Price is not > 0) LeavePriceOpen(row);
            if (sizeLabel is not null && sizeLabel.Bottom + 46 * k < frame.Height)
            {
                // The boxes end where the row's figures end; the label's own right edge is lost when OCR drops "(SCU)".
                var right = new[] { sizeLabel.Right, quantity?.Right ?? 0, price?.Right ?? 0 }.Max() + 16 * k;
                // The boxes run parallel to their label. The box around a sloping label is taller than its
                // letters, which reach its bottom at the lower end only; from there the slope is followed out to
                // where the boxes end.
                var letters = sizeLabel.Bottom + Math.Min(0, sizeLabel.Slope) * (sizeLabel.Right - sizeLabel.X) + sizeLabel.Slope * (right - sizeLabel.Right);
                row.SizeBand = Rectangle.FromLTRB((int)(right - 350 * k), (int)(letters + 10 * k), (int)right, (int)(letters + 46 * k));
                row.SizeSlope = sizeLabel.Slope;
            }
            row.Region = Around(left.Concat<Line?>([quantity, price, sizeLabel]), k, row.SizeBand);
            scan.Prices.Add(row);
        }

        // The player's own goods: the price is what the terminal pays, the quantity is only what the player carries.
        var mine = lines.Where(x => x.Right < shopLeft).ToList();
        foreach (var price in mine.Where(x => PricePattern().IsMatch(Tidy(x.Text))).OrderBy(x => x.Y))
        {
            var quantity = mine.FirstOrDefault(x => x.Y >= price.Y - 45 * k && x.Y <= price.Y - 8 * k && Math.Abs(x.Right - price.Right) < 40 * k && QuantityPattern().IsMatch(x.Text));
            var rowY = quantity?.CenterY ?? price.CenterY - 31 * k;
            var edge = (quantity ?? price).X - 5 * k;
            var named = mine.Where(x => x.Right < edge && Math.Abs(x.CenterY - rowY) < 16 * k && !IsLabel(x)).OrderBy(x => x.X).ToList();
            var name = CleanName(named.Select(x => x.Text));
            var section = mine.Where(x => x.Y < price.Y && x.Squashed is "INDEMAND" or "NODEMAND" or "CANNOTSELL").OrderBy(x => x.Y).LastOrDefault()?.Squashed;
            if (name.Length == 0 || section == "CANNOTSELL") continue;
            var row = new ScannedPrice
            {
                Name = name, IsBuy = false, FigureBand = FigureBand(quantity, price, k), Region = Around(named.Concat<Line?>([price, quantity]), k),
                // What the player is paid can differ from the price the terminal lists: 39,258 here against 39,000
                // under LOCAL MARKET VALUE on the same visit.
                Notes = { "From your own cargo: stays on this PC. UEX gets this price from the LOCAL MARKET VALUE tab." }
            };
            row.Price = Figure(PricePattern().Match(Tidy(price.Text)), "price", row.Doubts);
            if (row.Price is not > 0) LeavePriceOpen(row);
            if (section == "NODEMAND") row.Notes.Add("Listed under NO DEMAND.");
            scan.Prices.Add(row);
        }

        var yours = lines.FirstOrDefault(x => x.Has("YOUR", "INVENTOR"));
        if (yours is not null)
            scan.LocationHint = lines.Where(x => x.Y > yours.Bottom && x.Y < yours.Bottom + 70 * k && Math.Abs(x.X - yours.X) < 60 * k && !IsLabel(x))
                .OrderBy(x => x.Y).FirstOrDefault()?.Text.Trim() ?? "";
        // "Select Location" is the empty picker, not a place.
        if (scan.LocationHint.StartsWith("Select", StringComparison.OrdinalIgnoreCase)) scan.LocationHint = "";
        return scan;
    }

    // What a closer look at one row's figures shows: the price, and the quantity printed above it.
    public static (double? Price, double? Quantity) Figures(IEnumerable<OcrWord> words)
    {
        double? price = null, quantity = null;
        foreach (var text in Lines(words).OrderBy(x => x.Y).Select(x => Tidy(x.Text)))
        {
            if (PricePattern().Match(text) is { Success: true } paid) price ??= Figure(paid, "", []);
            else if (TrailingQuantityPattern().Match(text) is { Success: true } held) quantity ??= Figure(held, "", []);
        }
        return (price is > 0 ? price : null, quantity);
    }

    // A row whose price was not read is still shown, so the player sees it is there and can type the price in.
    public static void LeavePriceOpen(ScannedPrice row)
    {
        row.Price = null;
        if (!row.Doubts.Contains(MissingPrice)) row.Doubts.Add(MissingPrice);
    }

    // The number in the first group of a match. The terminal's zero has neither slash nor dot, so an "O" is the
    // same glyph and is taken as 0 without comment. Other look-alikes ("B.379" for "8,379") are restored too, but
    // that is a guess and leaves a doubt. Null for what is left of a number that lost its first digits ("OOO",
    // or ",800" where the terminal says 5,800): the rest of it looks like a price and is not one.
    private static double? Figure(Match match, string what, List<string> doubts)
    {
        var text = match.Groups[1].Value;
        var zeros = text.Replace('O', '0').Replace('o', '0');
        var repaired = zeros.Replace('B', '8').Replace('S', '5').Replace('l', '1').Replace('I', '1').Replace('i', '1').Replace('Z', '2');
        if (match.Groups["lost"].Success || repaired.Length > 1 && repaired[0] == '0' && char.IsDigit(repaired[1])) return null;
        var value = Number(repaired);
        if (repaired != zeros) doubts.Add($"The {what} was read as \"{text}\" and taken to be {value:#,0.##}: check it.");
        return value;
    }

    // "12, OOO/SCU": OCR sometimes opens a gap after the separator.
    private static string Tidy(string text) => GapInNumberPattern().Replace(text, "");

    // The quantity and price of a row, with room for the currency sign. When no price line was read, the box
    // reaches down and left to where one would be, so a closer look can still find it.
    private static Rectangle? FigureBand(Line? quantity, Line? price, double k)
    {
        var first = (quantity ?? price)!;
        var last = price ?? quantity!;
        return Rectangle.FromLTRB((int)(Math.Min(first.X, last.X) - (price is null ? 110 : 46) * k), (int)((quantity?.Y ?? price!.Y - 32 * k) - 8 * k),
            (int)(Math.Max(first.Right, last.Right) + 14 * k), (int)((price?.Bottom ?? quantity!.Bottom + 36 * k) + 8 * k));
    }

    private static void ReadConfirmation(TerminalScan scan, List<Line> lines, Line details, double k)
    {
        var price = lines.Where(x => x.Y > details.Bottom && x.Y < details.Bottom + 120 * k && PricePattern().IsMatch(x.Text)).OrderBy(x => x.Y).FirstOrDefault();
        var transaction = new ScannedTransaction
        {
            Name = price is null ? "" : CleanName(lines.Where(x => x.Right < price.X && Math.Abs(x.CenterY - price.CenterY) < 20 * k).OrderBy(x => x.X).Select(x => x.Text)),
            UnitPrice = price is null ? null : Figure(PricePattern().Match(price.Text), "price", scan.Warnings),
            IsPurchase = lines.Any(x => x.Has("UNLOADING")) ? false : lines.Any(x => x.Has("LOADING")) ? true : null,
            Quantity = lines.Where(x => x.Y > details.Bottom && QuantityPattern().IsMatch(x.Text)).OrderBy(x => x.Y)
                .Select(x => (int?)Figure(QuantityPattern().Match(x.Text), "quantity", scan.Warnings)).FirstOrDefault(),
            Fee = lines.Where(x => x.Has("AUTO", "LOAD")).Select(x => Number(NumberPattern().Matches(x.Text).LastOrDefault()?.Value)).FirstOrDefault(),
            Seconds = lines.Select(x => Duration(x.Text)).FirstOrDefault(x => x is not null)
        };
        if (lines.FirstOrDefault(x => x.Squashed == "CONFIRM") is { } confirm)
            transaction.TotalBand = Rectangle.FromLTRB((int)(confirm.X - 260 * k), (int)(confirm.Y - 12 * k), (int)(confirm.X - 10 * k), (int)(confirm.Bottom + 12 * k));
        scan.Transaction = transaction;
        if (transaction.Name.Length > 0 && transaction.UnitPrice is > 0)
            scan.Prices.Add(new ScannedPrice
            {
                Name = transaction.Name, IsBuy = transaction.IsPurchase, Price = transaction.UnitPrice,
                Region = Around(lines.Where(x => x.Y >= details.Y - 80 * k && x.Y <= details.Bottom + 400 * k && x.X >= details.X - 20 * k && x.X <= details.X + 900 * k), k),
                Notes = { "From a transaction confirmation: the price is exact, the stock is not shown." }
            });
    }

    // "8,379" and the misread "76.362" are thousands; "28,113.75" keeps its decimals. Null when there is no number.
    public static double? Number(string? text)
    {
        if (NumberPattern().Match(text ?? "") is not { Success: true } match) return null;
        var digits = Regex.Replace(match.Value.Replace(",", ""), @"\.(?=\d{3}(\D|$))", "");
        return double.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    // "00:02:12" is 132 seconds.
    public static int? Duration(string text) => DurationPattern().Match(text) is { Success: true } match
        ? int.Parse(match.Groups[1].Value) * 3600 + int.Parse(match.Groups[2].Value) * 60 + int.Parse(match.Groups[3].Value)
        : null;

    // UEX inventory status from the terminal's wording, tolerating misread letters such as "VERY LCW INVENTORY".
    // The text is the row's status lines run together, because OCR may split "OUT OF" from "STOCK".
    private static int? StatusCode(string text)
    {
        if (text.Contains("OUTOF") || text.Contains("EMPTY")) return 1;
        if (text.StartsWith("VERY")) return text[4..].StartsWith('H') ? 6 : 2;
        if (text.StartsWith("MAX") || text.Contains("FULL")) return 7;
        if (text.StartsWith("MED")) return 4;
        if (text.StartsWith("HIGH")) return 5;
        return text.StartsWith('L') ? 3 : null;
    }

    private static bool IsStatus(Line line) => (line.Squashed.Contains("INVENTOR") || line.Squashed.Contains("OUTOF") || line.Squashed == "STOCK") &&
                                               !line.Has("SHOP") && !line.Has("YOUR");

    private static bool IsLabel(Line line) => Labels.Contains(line.Squashed) || line.Squashed.StartsWith("QUALITY") || line.Has("CARGO", "SIZE") ||
                                              line.Squashed.StartsWith("TOTAL") || line.Has("TIME");

    // Drops the price-trend arrow that OCR reads as a stray letter in front of the name.
    private static string CleanName(IEnumerable<string> parts)
    {
        var words = string.Join(" ", parts).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && words[0].Length == 1 && !char.IsDigit(words[0][0])) words.RemoveAt(0);
        return string.Join(" ", words).Trim();
    }

    private static List<Line> Lines(IEnumerable<OcrWord> words)
    {
        var lines = new List<Line>();
        foreach (var group in words.GroupBy(x => x.Line))
        {
            var run = new List<OcrWord>();
            foreach (var word in group.OrderBy(x => x.X))
            {
                // One OCR line can run across both panels; a wide gap starts a new one.
                if (run.Count > 0 && word.X - run[^1].Right > 3 * Math.Max(word.Height, run[^1].Height)) Flush();
                run.Add(word);
            }
            Flush();

            void Flush()
            {
                // "RECYCLED MATERIAL COMPOSITE 0 SCU": a long name runs into its quantity. They are two things.
                if (run.Count >= 3 && run[^1].Text.Equals("SCU", StringComparison.OrdinalIgnoreCase) && FigurePattern().IsMatch(run[^2].Text) &&
                    run.Take(run.Count - 2).Any(x => x.Text.Count(char.IsLetter) >= 3 && !FigurePattern().IsMatch(x.Text)))
                {
                    Add(run.Take(run.Count - 2).ToList());
                    Add(run.Skip(run.Count - 2).ToList());
                }
                else if (run.Count > 0) Add(run);
                run = [];
            }

            void Add(List<OcrWord> part) =>
                lines.Add(new Line(string.Join(" ", part.Select(x => x.Text)), part.Min(x => x.X), part.Min(x => x.Y), part.Max(x => x.Right), part.Max(x => x.Y + x.Height))
                {
                    // From the middle of the first word to the middle of the last; one word alone says nothing.
                    Slope = part.Count < 2 ? 0 : (part[^1].CenterY - part[0].CenterY) / (part[^1].X + part[^1].Width / 2 - part[0].X - part[0].Width / 2)
                });
        }
        return lines;
    }

    // The box around some lines, with room for the icon left of a row's name.
    private static Rectangle? Around(IEnumerable<Line?> lines, double k, Rectangle? also = null)
    {
        var boxes = lines.OfType<Line>().Select(x => Box(x)!.Value).Concat(also is { } extra ? [extra] : []).ToList();
        if (boxes.Count == 0) return null;
        var all = boxes.Aggregate(Rectangle.Union);
        return Rectangle.FromLTRB((int)(all.Left - 80 * k), (int)(all.Top - 14 * k), (int)(all.Right + 14 * k), (int)(all.Bottom + 14 * k));
    }

    private static Rectangle? Box(Line line) => Rectangle.FromLTRB((int)line.X, (int)line.Y, (int)line.Right, (int)line.Bottom);

    private static string Squash(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    // B, O, o, S, l, I, i and Z are digits OCR got wrong; Figure turns them back.
    // The unit after the slash is SCU or Units, however OCR spelled it ("SClJ", "5CU", "8cu", "UNlT").
    // A separator in front of the number ("us, 800/SCU" for 5,800) means its first digits were lost.
    [GeneratedRegex(@"(?<lost>[.,]\s*)?([\dBOoSlIiZ][\dBOoSlIiZ.,]*)\s*/\s*([S5B8]\w{2,3}|[Uu][Nn]\w{2,3})")]
    private static partial Regex PricePattern();

    [GeneratedRegex(@"^[^\dBOoSlIiZ]{0,2}([\dBOoSlIiZ][\dBOoSlIiZ.,]*)\s*(?i:SCU|UNITS?)$")]
    private static partial Regex QuantityPattern();

    // The quantity at the end of a line that may start with the clipped end of a name.
    [GeneratedRegex(@"(?<![\w/.,])([\dBOoSlIiZ][\dBOoSlIiZ.,]*)\s*(?i:SCU)\s*$")]
    private static partial Regex TrailingQuantityPattern();

    [GeneratedRegex(@"^[\dBOoSlIiZ][\dBOoSlIiZ.,]*$")]
    private static partial Regex FigurePattern();

    [GeneratedRegex(@"(?<=[\dBOoSlIiZ][.,])\s+(?=[\dBOoSlIiZ])")]
    private static partial Regex GapInNumberPattern();

    [GeneratedRegex(@"\d[\d.,]*\d|\d")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\b(\d{1,2}):(\d{2}):(\d{2})\b")]
    private static partial Regex DurationPattern();
}
