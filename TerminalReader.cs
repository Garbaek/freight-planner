using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FreightCopilot;

// Reads a commodity-terminal screenshot: one OCR pass over the whole frame to find the rows, then closer looks
// at each row's figures, and pixels for what OCR cannot be trusted with (the crate-size boxes, the lit shop tab).
public static class TerminalReader
{
    // Small crops at these enlargements read the terminal's thin digits far better than the full-frame pass does.
    private static readonly (double Scale, bool HighContrast)[] CloserLooks = [(3, true), (4, true), (3, false)];

    public static async Task<TerminalScan> ReadAsync(LocalOcrService ocr, string imagePath, CancellationToken cancellation)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var shot = new Bitmap(stream);
        var frame = await ocr.ReadFrameAsync(shot, cancellation);
        var scan = TerminalScreenParser.Parse(frame);
        // A terminal seen from the side is read as if it were level, and every box the parser gives is a box on
        // that level picture. So the pixels are looked at on a level picture too: on the screenshot as it is, the
        // boxes are a row off and show the panel under the tabs instead of the tabs.
        using var level = frame.Angle == 0 ? null : Level(shot, frame.Angle);
        var image = level ?? shot;
        var k = image.Height / 1440.0;

        if (scan.Transaction is null)
        {
            // The headings in the shop list and the lit tab above it should say the same thing. Where they do
            // not, or neither says anything, the side stays open for the player to fill in.
            var lit = OpenTabIsBuy(image, scan.BuyTab, scan.ValueTab);
            foreach (var row in scan.Prices.Where(x => x.HasShopQuantity))
                row.IsBuy = row.IsBuy is { } listed && lit is { } open && listed != open ? null : row.IsBuy ?? lit;
        }

        // One OCR pass can read "8,379" as "8279" and look perfectly sure of it. Several readings of the same
        // spot rarely make the same mistake, so a figure counts only when most of them agree.
        foreach (var row in scan.Prices.Where(x => x.FigureBand is not null))
        {
            var prices = new List<double?> { row.Price };
            var quantities = new List<double?> { row.ShopScu };
            foreach (var (scale, highContrast) in CloserLooks)
            {
                var (price, quantity) = TerminalScreenParser.Figures(await ocr.ReadRegionAsync(image, row.FigureBand!.Value, scale, highContrast, cancellation));
                prices.Add(price);
                quantities.Add(quantity);
            }
            row.Price = Agree(prices, "price", row.Doubts);
            if (row.Price is null) TerminalScreenParser.LeavePriceOpen(row);
            if (row.HasShopQuantity) row.ShopScu = (int?)Agree(quantities, "shop quantity", row.Doubts);
        }

        foreach (var row in scan.Prices.Where(x => x.SizeBand is not null))
        {
            var band = row.SizeBand!.Value;
            var boxes = CrateSizeReader.Boxes(image, band, k, row.SizeSlope);
            row.ContainerSizes = CrateSizeReader.Resolve(boxes, [], k);
            // The box count alone leaves "1, 2" and "4, 8" apart only by their digits. A sloping row leaves the
            // band at its higher end, so the crop for reading them is that much taller.
            if (row.ContainerSizes.Length == 0 && boxes.Count > 0)
                row.ContainerSizes = CrateSizeReader.Resolve(boxes,
                    await ocr.ReadRegionAsync(image, Rectangle.Inflate(band, 0, (int)Math.Ceiling(Math.Abs(row.SizeSlope) * band.Width)), 4, false, cancellation), k);
        }

        if (scan.Transaction is { TotalBand: { } totalBand } transaction)
        {
            var words = await ocr.ReadRegionAsync(image, totalBand, 3, false, cancellation);
            transaction.Total = TerminalScreenParser.Number(words.LastOrDefault(x => x.Text.Any(char.IsDigit))?.Text);
            if (transaction is { Total: { } total, Quantity: { } quantity, UnitPrice: { } price } &&
                Math.Abs(total - (quantity * price + (transaction.Fee ?? 0))) > Math.Max(2, quantity))
                scan.Warnings.Add("The total does not equal quantity × price plus the fee. One of the numbers was misread.");
        }
        // The player is shown each row's part of the screenshot itself, which is not level.
        if (level is not null)
            foreach (var row in scan.Prices.Where(x => x.Region is not null))
                row.Region = OnScreenshot(row.Region!.Value, shot.Size, frame.Angle);
        return scan;
    }

    // The screenshot turned about its centre until the text is level, which is the picture the OCR boxes belong to.
    internal static Bitmap Level(Bitmap shot, double angle)
    {
        var level = new Bitmap(shot.Width, shot.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(level);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.TranslateTransform(shot.Width / 2f, shot.Height / 2f);
        graphics.RotateTransform((float)-angle);
        graphics.TranslateTransform(-shot.Width / 2f, -shot.Height / 2f);
        graphics.DrawImage(shot, 0, 0, shot.Width, shot.Height);
        return level;
    }

    // The part of the screenshot that a box on the level picture covers.
    internal static Rectangle OnScreenshot(Rectangle box, Size size, double angle)
    {
        var (sin, cos) = Math.SinCos(angle * Math.PI / 180);
        // Each corner as its distance from the centre, turned back by the angle.
        var corners = new[] { (X: box.Left, Y: box.Top), (X: box.Right, Y: box.Top), (X: box.Left, Y: box.Bottom), (X: box.Right, Y: box.Bottom) }
            .Select(p => (X: p.X - size.Width / 2.0, Y: p.Y - size.Height / 2.0))
            .Select(p => (X: size.Width / 2.0 + p.X * cos - p.Y * sin, Y: size.Height / 2.0 + p.X * sin + p.Y * cos)).ToList();
        return Rectangle.FromLTRB((int)corners.Min(p => p.X), (int)corners.Min(p => p.Y), (int)Math.Ceiling(corners.Max(p => p.X)), (int)Math.Ceiling(corners.Max(p => p.Y)));
    }

    // The value most readings agree on; the first reading is the full-frame pass, the others are closer looks,
    // which win a tie. Doubts the parser left about this figure are replaced by the verdict of the vote: none
    // when a clear majority agrees, one doubt naming the readings when they do not.
    public static double? Agree(IReadOnlyList<double?> readings, string what, List<string> doubts)
    {
        doubts.RemoveAll(x => x.StartsWith($"The {what} "));
        var seen = readings.Where(x => x is not null).Select(x => x!.Value).ToList();
        if (seen.Count == 0) return null;
        var ranked = seen.GroupBy(x => x).Select(x => (Value: x.Key, Count: x.Count(), Last: seen.LastIndexOf(x.Key)))
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.Last).ToList();
        var (value, count, _) = ranked[0];
        if (ranked.Count > 1 && count * 2 <= seen.Count)
            doubts.Add($"The {what} was read as {string.Join(" and as ", ranked.Select(x => x.Value.ToString("#,0.##")))}: check it.");
        else if (count == 1)
            doubts.Add($"The {what} {value:#,0.##} was read once only: check it.");
        return value;
    }

    // BUY lists what the shop sells; LOCAL MARKET VALUE lists what it pays for goods. The lit tab has a filled
    // background and the other is as dark as the panel. Null when a label was not found or neither tab stands
    // out: one label alone says nothing, and a guess here ends up as a report on the wrong side.
    internal static bool? OpenTabIsBuy(Bitmap image, Rectangle? buyTab, Rectangle? valueTab)
    {
        if (buyTab is not { } buy || valueTab is not { } value) return null;
        var (buyLight, valueLight) = (Background(image, buy), Background(image, value));
        return buyLight > valueLight * 1.4 ? true : valueLight > buyLight * 1.4 ? false : null;
    }

    // How light a label's tab is. The letters are lighter than either tab, and one terminal's bold font fills
    // more than half of the label's box with them, so the tab is taken from the darkest tenth of the box.
    private static double Background(Bitmap image, Rectangle label)
    {
        label.Intersect(new Rectangle(0, 0, image.Width, image.Height));
        var light = new List<double>();
        for (var x = label.Left; x < label.Right; x++)
        for (var y = label.Top; y < label.Bottom; y++)
            light.Add(CrateSizeReader.Luminance(image.GetPixel(x, y)));
        if (light.Count == 0) return 0;
        light.Sort();
        return light[light.Count / 10];
    }
}

// The row of crate-size boxes under "AVAILABLE CARGO SIZE". Single digits in boxes are what OCR misreads most,
// so the boxes are counted from the pixels: the game offers a consecutive run of 1, 2, 4, 8, 16, 24, 32, and how
// many boxes hold one digit and how many hold two usually settles which run it is.
public static class CrateSizeReader
{
    public sealed record Box(double Center, char Kind);

    private const char OneDigit = 'S', TwoDigits = 'D', Highlighted = 'F';

    public static double Luminance(Color color) => 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;

    // The boxes in the band, left to right. Empty when the band does not look like a row of evenly spaced boxes.
    // The band is where the row is at its right end; slope is how far the row drops per pixel to the right.
    public static List<Box> Boxes(Bitmap image, Rectangle band, double k, double slope = 0)
    {
        band.Intersect(new Rectangle(0, 0, image.Width, image.Height));
        if (band.Width <= 0 || band.Height <= 0) return [];
        var light = new double[band.Width, band.Height];
        double brightest = 0;
        for (var x = 0; x < band.Width; x++)
        {
            var top = band.Y + (int)Math.Round(slope * (x - band.Width));
            for (var y = Math.Max(0, -top); y < band.Height && top + y < image.Height; y++)
                brightest = Math.Max(brightest, light[x, y] = Luminance(image.GetPixel(band.X + x, top + y)));
        }
        // Digits are the brightest thing in the band. One terminal style draws its box outlines nearly as bright;
        // those are told apart by reaching into the top and bottom rows of the band, where no digit does.
        var threshold = Math.Min(100, brightest * 0.7);
        var edge = Math.Max(1, (int)Math.Round(4 * k));

        var runs = new List<(int Start, int End)>();
        for (var x = 0; x < band.Width; x++)
        {
            int ink = 0, outline = 0;
            for (var y = 0; y < band.Height; y++)
            {
                if (y < edge || y >= band.Height - edge) outline += light[x, y] > brightest * 0.5 ? 1 : 0;
                else if (light[x, y] > threshold) ink++;
            }
            if (ink < 2 || outline >= edge) continue;
            // The two digits of "16" stand a few pixels apart; neighbouring boxes stand much further apart.
            if (runs.Count > 0 && x - runs[^1].End <= 8 * k) runs[^1] = (runs[^1].Start, x);
            else runs.Add((x, x));
        }

        // Wider than a box can be: outlines that ran into one another, not something to count boxes in.
        if (runs.Any(x => x.End - x.Start > 60 * k)) return [];
        var boxes = runs.Where(x => x.End - x.Start >= 1).Select(x =>
        {
            var width = x.End - x.Start + 1;
            return new Box(band.X + (x.Start + x.End) / 2.0, width < 16 * k ? OneDigit : width < 32 * k ? TwoDigits : Highlighted);
        }).ToList();
        for (var i = 1; i < boxes.Count; i++)
            if (boxes[i].Center - boxes[i - 1].Center is var pitch && (pitch < 32 * k || pitch > 60 * k)) return [];
        return boxes;
    }

    // The crate sizes as "1,2,4,8", or "" when the boxes and the OCR words together still allow more than one answer.
    public static string Resolve(IReadOnlyList<Box> boxes, IReadOnlyList<OcrWord> words, double k)
    {
        var sizes = ObservationValidator.ContainerSizes;
        if (boxes.Count == 0 || boxes.Count > sizes.Length) return "";
        var candidates = Enumerable.Range(0, sizes.Length - boxes.Count + 1)
            .Where(start => boxes.Select((box, i) => box.Kind == Highlighted || (box.Kind == OneDigit) == (sizes[start + i] <= 8)).All(x => x))
            .Select(start => sizes.Skip(start).Take(boxes.Count).ToArray()).ToList();
        if (candidates.Count > 1)
        {
            var read = words.Select(word => (Size: ReadSize(word.Text), Index: Nearest(boxes, word.X + word.Width / 2, k))).Where(x => x.Size > 0 && x.Index >= 0).ToList();
            var scored = candidates.Select(x => (Sizes: x, Score: read.Sum(r => x[r.Index] == r.Size ? 1 : -1))).OrderByDescending(x => x.Score).ToList();
            candidates = scored[0].Score > 0 && scored[0].Score > scored[1].Score ? [scored[0].Sizes] : [];
        }
        return candidates.Count == 1 ? string.Join(",", candidates[0]) : "";
    }

    private static int Nearest(IReadOnlyList<Box> boxes, double x, double k)
    {
        var index = Enumerable.Range(0, boxes.Count).MinBy(i => Math.Abs(boxes[i].Center - x));
        return Math.Abs(boxes[index].Center - x) < 16 * k ? index : -1;
    }

    private static int ReadSize(string text)
    {
        var digits = text.Trim().Replace('B', '8').Replace('l', '1').Replace('I', '1').Replace('|', '1').Replace('Z', '2');
        return int.TryParse(digits, out var size) && ObservationValidator.ContainerSizes.Contains(size) ? size : 0;
    }
}
