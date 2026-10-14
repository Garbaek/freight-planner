using System.Text.Json;

namespace FreightCopilot.Tests;

// The fixtures are Windows OCR output and crops of real 2560x1440 terminal screenshots taken on 10 October 2026.
// The "tilted" ones are of terminals seen from the side, where the OCR engine gives its boxes for the picture
// turned level (OcrFrame.Angle).
public class TerminalScreenTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static OcrFrame Frame(string name) => JsonSerializer.Deserialize<OcrFrame>(File.ReadAllText(Fixture(name + ".json")))!;

    private static TerminalScan Scan(string name) => TerminalScreenParser.Parse(Frame(name));

    private static void AssertRow(ScannedPrice row, string name, bool? isBuy, double? price, int? shopScu, int? status)
    {
        Assert.Equal(name, row.Name);
        Assert.Equal(isBuy, row.IsBuy);
        Assert.Equal(price, row.Price);
        Assert.Equal(shopScu, row.ShopScu);
        Assert.Equal(status, row.Status);
    }

    [Fact]
    public void The_shop_panel_gives_buy_prices_with_stock_and_status()
    {
        var scan = Scan("levski-buy-and-sell");
        Assert.True(scan.IsTerminal);
        Assert.Null(scan.Transaction);
        Assert.Equal("Levski", scan.LocationHint);
        Assert.Equal(8, scan.Prices.Count);
        AssertRow(scan.Prices[0], "AGRICIUM", true, 8_379, 84, 3);
        AssertRow(scan.Prices[1], "CONSTRUCTION MATERIALS", true, 8_640, 29, 2);
        // "VERY LCW INVENTORY" in the OCR text.
        AssertRow(scan.Prices[2], "CORUNDUM", true, 2_822, 1, 2);
        AssertRow(scan.Prices[3], "SHIP AMMUNITION - SIZE 1", true, 6_868, 12_000, 7);
        Assert.All(scan.Prices.Take(3), x => Assert.NotNull(x.SizeBand));
        // The last row's crate boxes are below the edge of the panel.
        Assert.Null(scan.Prices[3].SizeBand);
    }

    [Fact]
    public void The_players_own_goods_give_sell_prices_but_never_a_stock_figure()
    {
        var sold = Scan("levski-buy-and-sell").Prices.Skip(4).ToList();
        AssertRow(sold[0], "RANTA DUNG", false, 39_258, null, null);
        // "76.362/SCU" in the OCR text.
        AssertRow(sold[1], "F)ROTA", false, 76_362, null, null);
        // The player held 6 SCU of Compboard; that is not what Levski has or wants.
        AssertRow(sold[2], "COMPBOARD", false, 34_650, null, null);
        AssertRow(sold[3], "CARBON", false, 375, null, null);
        // They are what the player is paid, not what the terminal lists, and say that they stay local.
        Assert.All(sold, x => Assert.Contains(x.Notes, note => note.StartsWith("From your own cargo")));
        Assert.All(sold, x => Assert.False(x.HasShopQuantity));
    }

    [Fact]
    public void A_digit_read_as_a_letter_is_restored_and_flagged()
    {
        var scan = Scan("levski-shop-only");
        Assert.Equal("", scan.LocationHint);
        Assert.Equal(6, scan.Prices.Count);
        // "B.379/SCU" in the OCR text.
        AssertRow(scan.Prices[0], "AGRICIUM", true, 8_379, 58, 2);
        Assert.Contains("B.379", Assert.Single(scan.Prices[0].Doubts));
        AssertRow(scan.Prices[1], "CONSTRUCTION MATERIALS", true, 8_640, 86, 3);
        Assert.Empty(scan.Prices[1].Doubts);
        AssertRow(scan.Prices[2], "CORUNDUM", true, 2_822, 1, 2);
        // "6.86B/SCU" in the OCR text.
        AssertRow(scan.Prices[3], "SHIP AMMUNITION - SIZE 1", true, 6_868, 12_000, 7);
        Assert.Contains("6.86B", Assert.Single(scan.Prices[3].Doubts));
        AssertRow(scan.Prices[4], "SHIP AMMUNITION - SIZE 2", true, 7_126, 12_000, 7);
        // The sixth row is cut off before its price. It is shown without one rather than left out or guessed.
        AssertRow(scan.Prices[5], "SHIP AMMUNITION", true, null, 12_000, null);
        Assert.Equal(TerminalScreenParser.MissingPrice, Assert.Single(scan.Prices[5].Doubts));
    }

    // The second terminal style: no SHOP QUANTITY label, name and quantity on one line, LOCAL MARKET VALUE open.
    // These are what the full-frame pass alone gives; TerminalReader then looks closer at each row's figures.
    [Fact]
    public void Rows_are_found_without_a_quantity_label_and_are_sell_prices_under_in_demand()
    {
        var scan = Scan("market-value-in-demand");
        Assert.Equal("", scan.LocationHint);
        Assert.Equal(5, scan.Prices.Count);
        // The full-frame pass lost Waste's price line altogether; the closer look finds 370.
        AssertRow(scan.Prices[0], "VASTE", false, null, 0, 1);
        Assert.Equal(TerminalScreenParser.MissingPrice, Assert.Single(scan.Prices[0].Doubts));
        AssertRow(scan.Prices[1], "DISTILLED SPIRITS", false, 1_900, 0, 1);
        // "4,OOO/SCU" in the OCR text: this terminal's zero is the letter O's twin, so it is no guess.
        AssertRow(scan.Prices[2], "TIN", false, 4_000, 0, 1);
        Assert.Empty(scan.Prices[2].Doubts);
        // "RECYCLED MATERIAL COMPOSITE O SCU" is one OCR line.
        AssertRow(scan.Prices[3], "RECYCLED MATERIAL COMPOSITE", false, 7_100, 0, 1);
        // Cut off at the bottom of the panel.
        AssertRow(scan.Prices[4], "CONSTRUCTION MATERIALS", false, null, 17, 1);
        Assert.All(scan.Prices, x => Assert.NotNull(x.FigureBand));
        Assert.All(scan.Prices, x => Assert.True(x.HasShopQuantity));
        Assert.All(scan.Prices.Take(4), x => Assert.NotNull(x.SizeBand));
    }

    [Fact]
    public void A_row_under_no_demand_is_a_sell_price_and_says_so()
    {
        var scan = Scan("market-value-no-demand");
        Assert.Equal(4, scan.Prices.Count);
        // No heading above the first rows, but one tab is open at a time: NO DEMAND further down settles them too.
        AssertRow(scan.Prices[0], "CONSTRUCTION MATERIALS", false, 12_000, 17, 1);
        // "ÄII/SCU" in the OCR text.
        AssertRow(scan.Prices[1], "SHIP AMMUNITION", false, 11, 0, 1);
        Assert.Contains("\"II\"", Assert.Single(scan.Prices[1].Doubts));
        // "OOO/SCU" is what was left of 11,000. Three zeros are no price.
        AssertRow(scan.Prices[2], "ORGANICS", false, null, 0, 1);
        AssertRow(scan.Prices[3], "GOLD", false, 31_000, 250, 7);
        Assert.Equal("Listed under NO DEMAND.", Assert.Single(scan.Prices[3].Notes));
        Assert.NotNull(scan.BuyTab);
        Assert.NotNull(scan.ValueTab);
    }

    // The screenshot of the report UEX declined on 11 October 2026 for its transaction type: LOCAL MARKET VALUE
    // open on a terminal seen from the side. Its NO DEMAND heading starts left of the panel's own title.
    [Fact]
    public void A_heading_anywhere_in_the_list_makes_every_shop_row_a_sell_price()
    {
        var scan = Scan("levski-tilted-market-value");
        Assert.Equal("Levski", scan.LocationHint);
        var shop = scan.Prices.Where(x => x.HasShopQuantity).ToList();
        Assert.Equal(4, shop.Count);
        AssertRow(shop[0], "ASLARITE", false, 5_100, 0, 1);
        AssertRow(shop[1], "OURATITE", false, 46_000, 0, 1);
        // "93,000/5ClJ" and "25,000/5CU" in the OCR text.
        AssertRow(shop[2], "ATLASIUM", false, 93_000, 36, 7);
        AssertRow(shop[3], "DYMANTIUM", false, 25_000, 128, 7);
        Assert.All(shop.Take(2), x => Assert.Empty(x.Notes));
        Assert.All(shop.Skip(2), x => Assert.Equal("Listed under NO DEMAND.", Assert.Single(x.Notes)));
    }

    [Fact]
    public void A_list_without_a_heading_leaves_the_side_to_the_lit_tab()
    {
        var value = Scan("kiosk-tilted-market-value");
        Assert.Equal(4, value.Prices.Count);
        Assert.All(value.Prices, x => Assert.Null(x.IsBuy));
        // IN STOCK is a heading of the BUY tab. "LOCAL HARKET VALUE" in the OCR text is still the other tab's label.
        var buy = Scan("kiosk-tilted-buy");
        Assert.Equal(3, buy.Prices.Count);
        Assert.All(buy.Prices, x => Assert.True(x.IsBuy));
        Assert.NotNull(buy.ValueTab);
    }

    // Screenshots that are black except around the two tabs. The first three are of terminals seen from the side:
    // their OCR boxes belong to the picture turned level, and lie on the panel under the tabs on the screenshot.
    [Theory]
    [InlineData("levski-tilted-market-value", false)]
    [InlineData("kiosk-tilted-market-value", false)]
    [InlineData("kiosk-tilted-buy", true)]
    [InlineData("pss-alpha-buy", true)]
    public void The_lit_tab_says_whether_the_shop_rows_are_buy_prices(string name, bool isBuy)
    {
        var frame = Frame(name);
        var scan = TerminalScreenParser.Parse(frame);
        using var shot = new Bitmap(Fixture(name + "-tabs.png"));
        using var level = TerminalReader.Level(shot, frame.Angle);
        Assert.Equal(isBuy, TerminalReader.OpenTabIsBuy(level, scan.BuyTab, scan.ValueTab));
    }

    [Fact]
    public void One_tab_label_alone_does_not_say_which_tab_is_lit()
    {
        var scan = Scan("pss-alpha-buy");
        using var shot = new Bitmap(Fixture("pss-alpha-buy-tabs.png"));
        Assert.Null(TerminalReader.OpenTabIsBuy(shot, scan.BuyTab, null));
        Assert.Null(TerminalReader.OpenTabIsBuy(shot, null, scan.ValueTab));
    }

    [Fact]
    public void A_box_on_the_level_picture_is_found_again_on_the_screenshot()
    {
        // The BUY label of the declined screenshot: read at 1950, 315 on the level picture, and 23 pixels higher
        // on the screenshot, which is turned by 2 degrees.
        var onScreenshot = TerminalReader.OnScreenshot(new Rectangle(1950, 315, 44, 21), new Size(2560, 1440), -2);
        Assert.True(onScreenshot.Contains(1958, 301));
        Assert.False(onScreenshot.Contains(1972, 325));
        Assert.Equal(new Rectangle(10, 20, 30, 40), TerminalReader.OnScreenshot(new Rectangle(10, 20, 30, 40), new Size(2560, 1440), 0));
    }

    private static OcrWord[] Words(params string[] lines) =>
        lines.SelectMany((line, row) => line.Split(' ').Select((word, i) => new OcrWord(word, row, 10 + i * 60, 10 + row * 30, 50, 16))).ToArray();

    private static void AssertFigures(double? price, double? quantity, params string[] lines)
    {
        var read = TerminalScreenParser.Figures(Words(lines));
        Assert.Equal(price, read.Price);
        Assert.Equal(quantity, read.Quantity);
    }

    // Each of these is what Windows OCR really returned for a crop of one row.
    [Fact]
    public void A_closer_look_reads_the_price_and_the_quantity_above_it()
    {
        AssertFigures(11_000, 0, "o SCU", "ÄII,OOO/SCU");
        AssertFigures(12_000, 17, "LS", "17 SCU", "u 12, OOO/SCU");
        AssertFigures(8_379, 58, "58 SCU", "Ä 8.379/ SCU");
        AssertFigures(6_868, 12_000, "12,000 SCU", "6.86B/SClJ");
        AssertFigures(31_000, 250, "250 SCU", "Ä31,OOO/SCU");
        // The end of a long name clipped into the crop.
        AssertFigures(7_100, 0, "SITE O SCU", "Ä7,100/SCU");
        // Another terminal's font: its 1 comes back as an i, the S of SCU as a 5.
        AssertFigures(1_800, 0, "o SCU", "Äi.800/SCU");
        AssertFigures(4_293, 12_000, "12,000 SCU", "Ä 4,293/5ClJ");
    }

    [Fact]
    public void A_closer_look_that_shows_no_figure_says_so()
    {
        // Three zeros are what is left of 11,000, not a price.
        AssertFigures(null, 0, "o SCU", "OOO/SCU");
        AssertFigures(null, 250, "250 SCU");
        // 5,800 that lost its first digit. What is left reads like a price of 800.
        AssertFigures(null, 0, "o scu", "us, 800/SCU");
        AssertFigures(null, null, "nnn/er.ll");
        AssertFigures(null, null);
    }

    [Fact]
    public void Readings_that_agree_leave_no_doubt()
    {
        var doubts = new List<string> { "The price was read as \"B.379\" and taken to be 8,379: check it.", "Another matter." };
        // The full-frame pass first, then three closer looks.
        Assert.Equal(8_379, TerminalReader.Agree([8_379, 8_379, 8_379, 8_379], "price", doubts));
        Assert.Equal("Another matter.", Assert.Single(doubts));
    }

    [Fact]
    public void One_stray_reading_is_outvoted()
    {
        var doubts = new List<string>();
        // One pass read 8,379 as 8279 with no sign of trouble.
        Assert.Equal(8_379, TerminalReader.Agree([8_279, 8_379, 8_379, 8_379], "price", doubts));
        Assert.Empty(doubts);
        Assert.Equal(11_000, TerminalReader.Agree([null, 11_000, 11_000, null], "price", doubts));
        Assert.Empty(doubts);
    }

    [Fact]
    public void Readings_that_disagree_leave_a_doubt_naming_them()
    {
        var doubts = new List<string>();
        Assert.Equal(8_379, TerminalReader.Agree([8_279, 8_379, 8_279, 8_379], "price", doubts));
        var doubt = Assert.Single(doubts);
        Assert.Contains(8_379.ToString("#,0"), doubt);
        Assert.Contains(8_279.ToString("#,0"), doubt);

        doubts.Clear();
        Assert.Equal(370, TerminalReader.Agree([null, 370, null, null], "price", doubts));
        Assert.Contains("once only", Assert.Single(doubts));

        doubts.Clear();
        Assert.Null(TerminalReader.Agree([null, null, null, null], "shop quantity", doubts));
        Assert.Empty(doubts);
    }

    [Fact]
    public void A_confirmation_is_a_transaction_not_a_stock_report()
    {
        var scan = Scan("pss-lambda-confirmation");
        var transaction = scan.Transaction!;
        Assert.Equal("COMPBOARD", transaction.Name);
        Assert.True(transaction.IsPurchase);
        Assert.Equal(28_113.75, transaction.UnitPrice);
        Assert.Equal(6, transaction.Quantity);
        Assert.Equal(330, transaction.Fee);
        // The loading time is in a font Windows OCR cannot read; it stays unknown.
        Assert.Null(transaction.Seconds);
        Assert.NotNull(transaction.TotalBand);

        var price = Assert.Single(scan.Prices);
        AssertRow(price, "COMPBOARD", true, 28_113.75, null, null);
    }

    [Fact]
    public void A_screen_without_a_terminal_gives_nothing()
    {
        var scan = TerminalScreenParser.Parse(new OcrFrame(2560, 1440,
        [
            new OcrWord("CARGO", 0, 1395, 767, 70, 14), new OcrWord("BAY", 0, 1470, 767, 40, 14),
            new OcrWord("C.O.", 1, 1330, 1308, 40, 12), new OcrWord("NOMAD", 1, 1376, 1308, 66, 12)
        ]));
        Assert.False(scan.IsTerminal);
        Assert.Empty(scan.Prices);
    }

    [Theory]
    [InlineData("8,379", 8_379)]
    [InlineData("76.362", 76_362)]
    [InlineData("28,113.75", 28_113.75)]
    [InlineData("12000", 12_000)]
    [InlineData("1.234.567", 1_234_567)]
    [InlineData("Ä 207,900", 207_900)]
    [InlineData("375", 375)]
    [InlineData("0.5", 0.5)]
    public void Numbers_are_read_with_either_thousands_separator(string text, double expected) => Assert.Equal(expected, TerminalScreenParser.Number(text));

    [Fact]
    public void Text_without_a_number_is_not_a_number()
    {
        Assert.Null(TerminalScreenParser.Number("MAX"));
        Assert.Null(TerminalScreenParser.Number(null));
    }

    [Fact]
    public void Durations_are_read_as_seconds()
    {
        Assert.Equal(79, TerminalScreenParser.Duration("Est. Unloading Time 00:01:19"));
        Assert.Equal(3_725, TerminalScreenParser.Duration("01:02:05"));
        Assert.Null(TerminalScreenParser.Duration("EST. LOADING TIME"));
    }

    [Fact]
    public void Terminal_screenshots_are_told_apart_from_contracts()
    {
        Assert.True(TerminalScreenParser.LooksLikeTerminal("[HEADER]\r\nSHOP INVENTORY\r\nBUY LOCAL MARKET VALUE"));
        Assert.True(TerminalScreenParser.LooksLikeTerminal("CONFIRMATION\nTRANSACTION DETAILS"));
        Assert.False(TerminalScreenParser.LooksLikeTerminal("[DETAILS]\nReward 61,500\nDeliver 12 SCU of Stims to Levski"));
    }

    [Theory]
    [InlineData("levski-buy-and-sell-sizes-agricium.png", "1,2,4,8,16")]
    [InlineData("levski-buy-and-sell-sizes-construction-materials.png", "1,2,4,8")]
    [InlineData("levski-buy-and-sell-sizes-corundum.png", "1,2,4,8,16,24,32")]
    // The second terminal style draws its box outlines nearly as bright as the digits.
    [InlineData("market-value-in-demand-sizes-vaste.png", "1,2,4,8,16,24,32")]
    [InlineData("market-value-in-demand-sizes-distilled-spirits.png", "1,2,4,8,16,24,32")]
    [InlineData("market-value-in-demand-sizes-tin.png", "1,2,4,8,16,24,32")]
    [InlineData("market-value-in-demand-sizes-recycled-material-composite.png", "1,2,4,8,16,24,32")]
    [InlineData("market-value-no-demand-sizes-construction-materials.png", "1,2,4,8,16,24,32")]
    [InlineData("market-value-no-demand-sizes-ship-ammunition.png", "1,2,4,8,16,24,32")]
    [InlineData("market-value-no-demand-sizes-organics.png", "1,2,4,8,16,24,32")]
    [InlineData("market-value-no-demand-sizes-gold.png", "1,2,4,8,16,24,32")]
    public void Crate_sizes_are_counted_from_the_boxes(string file, string expected)
    {
        using var image = new Bitmap(Fixture(file));
        var boxes = CrateSizeReader.Boxes(image, new Rectangle(0, 0, image.Width, image.Height), 1);
        Assert.Equal(expected, CrateSizeReader.Resolve(boxes, [], 1));
    }

    // A row seen from the side: the band is where the boxes are at its right end, and 16 pixels too low at its left.
    [Fact]
    public void A_sloping_row_of_boxes_is_followed()
    {
        using var image = new Bitmap(Fixture("levski-sloping-sizes-corundum.png"));
        var boxes = CrateSizeReader.Boxes(image, new Rectangle(0, 24, image.Width, 36), 1, 0.045);
        Assert.Equal("1,2,4,8,16,24,32", CrateSizeReader.Resolve(boxes, [], 1));
    }

    // One terminal's outlines are as bright as its digits and do not reach the band's edges, so the whole row is
    // one bright stretch. One digit read by OCR must not turn that into a single crate size.
    [Fact]
    public void Outlines_that_run_into_one_another_are_not_boxes()
    {
        using var image = new Bitmap(Fixture("kiosk-tilted-buy-sizes-fluorine.png"));
        Assert.Empty(CrateSizeReader.Boxes(image, new Rectangle(0, 24, image.Width, 36), 1, 0.002));
    }

    private static List<CrateSizeReader.Box> Row(params char[] kinds) => kinds.Select((kind, i) => new CrateSizeReader.Box(100 + i * 46, kind)).ToList();

    [Fact]
    public void Two_single_digit_boxes_alone_are_not_enough()
    {
        // "1, 2", "2, 4" and "4, 8" all look like this.
        Assert.Equal("", CrateSizeReader.Resolve(Row('S', 'S'), [], 1));
    }

    [Fact]
    public void A_digit_read_by_ocr_settles_which_boxes_they_are()
    {
        Assert.Equal("1,2", CrateSizeReader.Resolve(Row('S', 'S'), [new OcrWord("2", 0, 140, 10, 10, 16)], 1));
        Assert.Equal("4,8", CrateSizeReader.Resolve(Row('S', 'S'), [new OcrWord("B", 0, 140, 10, 10, 16)], 1));
        Assert.Equal("8,16,24,32", CrateSizeReader.Resolve(Row('S', 'D', 'D', 'D'), [], 1));
        Assert.Equal("16,24", CrateSizeReader.Resolve(Row('D', 'D'), [new OcrWord("16", 0, 92, 10, 18, 16)], 1));
    }

    [Fact]
    public void Contradicting_or_missing_digits_leave_the_sizes_unknown()
    {
        Assert.Equal("", CrateSizeReader.Resolve(Row('S', 'S'), [new OcrWord("1", 0, 95, 10, 10, 16), new OcrWord("8", 0, 141, 10, 10, 16)], 1));
        Assert.Equal("", CrateSizeReader.Resolve(Row('D'), [new OcrWord("x6", 0, 95, 10, 10, 16)], 1));
        Assert.Equal("", CrateSizeReader.Resolve([], [], 1));
    }

    private static readonly UexCommodity[] Commodities =
    [
        new() { Id = 1, Name = "Agricium" }, new() { Id = 2, Name = "Agricium (Ore)" }, new() { Id = 18, Name = "Compboard" },
        new() { Id = 60, Name = "Prota" }, new() { Id = 61, Name = "Ranta Dung" }, new() { Id = 91, Name = "Construction Materials" },
        new() { Id = 140, Name = "Ship Ammunition - Size 1" }, new() { Id = 141, Name = "Ship Ammunition - Size 2" }, new() { Id = 20, Name = "Corundum" },
        new() { Id = 80, Name = "Waste" }, new() { Id = 81, Name = "Tin" }, new() { Id = 82, Name = "Tin (Ore)" }, new() { Id = 83, Name = "Recycled Material Composite" }
    ];

    [Theory]
    [InlineData("AGRICIUM", 1)]
    [InlineData("F)ROTA", 60)]
    [InlineData("CONSTRUCTION MATERIALS", 91)]
    [InlineData("SHIP AMMUNITION - SIZE 2", 141)]
    [InlineData("CORUNOUM", 20)]
    [InlineData("VASTE", 80)]
    [InlineData("TIN", 81)]
    [InlineData("RECYCLED MATERIAL COMPOSITE", 83)]
    public void Ocr_names_find_their_commodity(string text, int expected) => Assert.Equal(expected, NameMatcher.Commodity(text, Commodities)!.Id);

    [Theory]
    [InlineData("SHIP AMMUNITION")]
    [InlineData("QUANTAINIUM")]
    [InlineData("A")]
    public void An_unclear_name_matches_nothing(string text) => Assert.Null(NameMatcher.Commodity(text, Commodities));

    [Fact]
    public void The_inventory_picker_names_the_terminal_only_when_it_shows_a_station()
    {
        UexTerminal[] terminals =
        [
            new() { Id = 778, Name = "Levski", Nickname = "Levski", Displayname = "Levski", StarSystemName = "Nyx" },
            new() { Id = 843, Name = "Admin - People's Service Station Lambda", Nickname = "PSS Lambda", Displayname = "People's Service Station Lambda", StarSystemName = "Nyx" }
        ];
        Assert.Equal(778, NameMatcher.Terminal("Levski", terminals)!.Id);
        Assert.Equal(843, NameMatcher.Terminal("People's Service Station Lambda", terminals)!.Id);
        Assert.Null(NameMatcher.Terminal("C.O. Nomad Teach's Special", terminals));
    }
}
