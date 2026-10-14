namespace FreightCopilot;

// What the main window lends the trading window. SendRunToTablet answers with a line for the status bar;
// the tablet link itself stays with the main window.
public sealed record TradeHost(TradeSettings Settings, LocalOcrService? Ocr, IReadOnlyList<ShipProfile> Ships, Func<ShipProfile> Ship,
    Func<string> ScreenshotFolder, Action<string> SetScreenshotFolder, Action SaveSettings, Func<TradeRun, Task<string>> SendRunToTablet,
    Func<bool, Task<string>> SetTradeTracker);

// What the trading tabs share: UEX's lists, the local market database and the settings.
public sealed class TradeContext(TradeHost host, UexClient uex, MarketStore? store)
{
    private Dictionary<(int Terminal, int Commodity), UexPrice> _prices = [];

    public TradeHost Host { get; } = host;
    public UexClient Uex { get; } = uex;
    // False when market.db could not be opened; Trade ideas then works from UEX alone.
    public bool HasStore => store is not null;
    public MarketStore Store => store ?? throw new InvalidOperationException("The local market database is not available.");
    public TradeSettings Settings => Host.Settings;
    public List<UexTerminal> Terminals { get; private set; } = [];
    public List<UexCommodity> Commodities { get; private set; } = [];
    // Null when UEX's price list could not be loaded; nothing is shown in its place.
    public DateTime? PricesFetchedUtc { get; private set; }

    // Raised when UEX's lists arrive and whenever observations or trades were saved.
    public event Action? Changed;

    public void Load(List<UexTerminal> terminals, List<UexCommodity> commodities, UexData<UexPrice>? prices)
    {
        Terminals = terminals;
        Commodities = commodities.Where(x => x.Name is { Length: > 0 }).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _prices = (prices?.Rows ?? []).GroupBy(x => (x.IdTerminal, x.IdCommodity)).ToDictionary(x => x.Key, x => x.First());
        PricesFetchedUtc = prices?.FetchedUtc;
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    public UexPrice? Price(int terminalId, int commodityId) => _prices.GetValueOrDefault((terminalId, commodityId));

    // UEX's last reported price for one side of a commodity at a terminal, or null when it has none.
    public PriceQuote? UexQuote(int terminalId, int commodityId, bool buy) => Price(terminalId, commodityId)?.Quote(buy);

    public MarketObservation NewObservation(UexTerminal terminal, UexCommodity commodity) => new()
    {
        StarSystem = terminal.StarSystemName ?? "", TerminalId = terminal.Id, TerminalName = terminal.Label,
        CommodityId = commodity.Id, CommodityName = commodity.Name ?? ""
    };
}

internal static class TradeUi
{
    public static readonly Color Dark = Color.FromArgb(20, 31, 46);
    public static readonly Color Muted = Color.FromArgb(188, 204, 220);
    public static readonly Color Mine = Color.FromArgb(226, 244, 229);
    public static readonly string[] StatusNames = ["", "1 Out of stock", "2 Very low", "3 Low", "4 Medium", "5 High", "6 Very high", "7 Maximum"];

    public static DataGridView Grid(bool readOnly = true) => new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        ReadOnly = readOnly,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        BackgroundColor = Color.White
    };

    public static DataGridViewTextBoxColumn Number(string header, int width, string toolTip = "", string format = "N0") => new()
    {
        HeaderText = header, Width = width, ValueType = typeof(double), ToolTipText = toolTip,
        DefaultCellStyle = new DataGridViewCellStyle { Format = format, Alignment = DataGridViewContentAlignment.MiddleRight }
    };

    public static DataGridViewTextBoxColumn Text(string header, int width, string toolTip = "") => new() { HeaderText = header, Width = width, ToolTipText = toolTip };

    public static DataGridViewTextBoxColumn Fill(string header, int minimumWidth) =>
        new() { HeaderText = header, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = minimumWidth };

    public static FlowLayoutPanel Bar(DockStyle dock, params Control[] controls)
    {
        var bar = new FlowLayoutPanel { Dock = dock, Height = 46, Padding = new Padding(10, 6, 10, 6), BackColor = Color.White, WrapContents = false };
        bar.Controls.AddRange(controls);
        return bar;
    }

    // A splitter that keeps one panel at a fixed size. The size is applied once the container has been laid
    // out, because a distance set before that is rescaled away.
    public static SplitContainer Split(Orientation orientation, int firstPanel = 0, int secondPanel = 0)
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = orientation, FixedPanel = firstPanel > 0 ? FixedPanel.Panel1 : FixedPanel.Panel2 };
        var placed = false;
        split.SizeChanged += (_, _) =>
        {
            var length = orientation == Orientation.Vertical ? split.Width : split.Height;
            var distance = firstPanel > 0 ? firstPanel : length - secondPanel - split.SplitterWidth;
            if (placed || distance < split.Panel1MinSize || distance > length - split.Panel2MinSize - split.SplitterWidth) return;
            split.SplitterDistance = distance;
            placed = true;
        };
        return split;
    }

    public static Button Button(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 1, 6, 0) };

    public static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 0) };

    public static string Source(PriceSource source) => source switch
    {
        PriceSource.Uex => "UEX",
        PriceSource.Manual => "Mine (typed)",
        _ => "Mine (screenshot)"
    };

    public static string Status(int? code) => code is >= 1 and <= 7 ? StatusNames[code.Value] : "";

    // "12 min", "5 h" or "3 d" since the time, or "" when it is unknown.
    public static string Age(DateTime? utc, DateTime nowUtc)
    {
        if (utc is not { } time) return "";
        var age = nowUtc - time;
        return age < TimeSpan.FromMinutes(1) ? "now" : age < TimeSpan.FromHours(1) ? $"{age.TotalMinutes:N0} min"
            : age < TimeSpan.FromHours(48) ? $"{age.TotalHours:N0} h" : $"{age.TotalDays:N0} d";
    }

    public static string When(DateTime utc) => utc.ToLocalTime().ToString("d MMM HH:mm");

    // "2:12" for 132 seconds; "" for an unknown time.
    public static string Duration(int? seconds) => seconds is > 0 ? seconds >= 3600
        ? $"{seconds / 3600}:{seconds % 3600 / 60:D2}:{seconds % 60:D2}" : $"{seconds / 60}:{seconds % 60:D2}" : "";

    // Reads "2:12", "1:02:05" or plain minutes ("26"). Null for an empty box; -1 when it is not a time.
    public static int? ParseDuration(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        var parts = text.Split(':');
        if (parts.Length > 3 || parts.Any(x => !int.TryParse(x, out var part) || part < 0)) return -1;
        var numbers = parts.Select(int.Parse).ToArray();
        return numbers.Length switch { 1 => numbers[0] * 60, 2 => numbers[0] * 60 + numbers[1], _ => numbers[0] * 3600 + numbers[1] * 60 + numbers[2] };
    }

    public static void Fill<T>(ComboBox box, IEnumerable<T> items, Func<T, bool>? select = null) where T : class
    {
        box.BeginUpdate();
        box.Items.Clear();
        box.Items.AddRange(items.ToArray<object>());
        box.EndUpdate();
        if (select is not null) box.SelectedItem = box.Items.OfType<T>().FirstOrDefault(select);
    }
}
