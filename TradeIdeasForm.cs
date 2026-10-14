namespace FreightCopilot;

public sealed class TradeIdeasForm : Form
{
    private const int SellAtColumn = 1, BuyColumn = 2, SellColumn = 3, CratesColumn = 8, ConfidenceColumn = 11;

    // Title and subtitle of the dark header, per tab.
    private readonly (string Title, string Text)[] _headers;
    private readonly ShipProfile _ship;
    private readonly TradeContext _context;
    private readonly MarketReporterPanel? _reporter;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Label _title = new() { ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 18), AutoSize = true, Location = new Point(20, 12) };
    private readonly Label _subtitle = new() { ForeColor = TradeUi.Muted, AutoSize = true, Location = new Point(22, 46) };
    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Type to find your station" };
    private readonly ListBox _stations = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly CheckBox _otherSystems = new() { Text = "Include other star systems", AutoSize = true, Margin = new Padding(3, 6, 12, 0) };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(3, 3, 12, 0) };
    private readonly Button _quickReport = new() { Text = "Quick report", AutoSize = true, Margin = new Padding(3, 1, 12, 0) };
    private readonly Button _sendTablet = new() { Text = "Start run", AutoSize = true, Enabled = false, Margin = new Padding(3, 1, 12, 0) };
    private readonly Label _status = new() { Text = "Loading stations from UEX…", AutoSize = true, Margin = new Padding(3, 8, 3, 0) };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        ReadOnly = true,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        BackgroundColor = Color.White
    };

    private List<UexTerminal> _terminals = [];
    private HashSet<int> _illegal = [];
    private UexData<UexRoute>? _routes;
    private UexTerminal? _origin;
    private int _request;

    // The station picked last, so the window can reopen on it.
    public string Station { get; private set; }

    public TradeIdeasForm(ShipProfile ship, string appDirectory, string station, TradeHost host)
    {
        _ship = ship;
        Station = station;
        MarketStore? store = null;
        string? storeProblem = null;
        try { store = new MarketStore(appDirectory); }
        catch (Exception ex) { storeProblem = ex.Message; }
        _context = new TradeContext(host, new UexClient(appDirectory), store);
        Text = "Freight Planner — Trading";
        Width = 1640;
        Height = 780;
        MinimumSize = new Size(1000, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        _headers =
        [
            ("WHAT CAN I HAUL FROM HERE?", $"Buy-and-sell runs for the {ship.Name} ({ship.CapacityScu:N0} SCU, {ShipCatalog.SummarizeCargo(ship)}). Prices are UEX community reports and your own confirmed observations; treat the profit as an estimate."),
            ("MARKET REPORTER", "Record what a terminal really charges and pays: type it in or read a screenshot, check it, save it, and send it to UEX if you want."),
            ("TRADING JOURNAL", "The runs you actually flew and what they really earned."),
            ("MARKET HISTORY", "Every price you saved, next to what UEX had at the time."),
            ("SETTINGS", "UEX account, screenshots, and how your own prices are used.")
        ];

        var header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = TradeUi.Dark };
        header.Controls.Add(_title);
        header.Controls.Add(_subtitle);

        var left = new Panel { Dock = DockStyle.Left, Width = 250, Padding = new Padding(10) };
        left.Controls.Add(_stations);
        left.Controls.Add(_search);
        left.Controls.Add(new Label { Text = "I am at", Dock = DockStyle.Top, Height = 26 });

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Buy here", Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sell at", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 180 });
        _grid.Columns.Add(Number("Buy / SCU", 78));
        _grid.Columns.Add(Number("Sell / SCU", 78));
        _grid.Columns.Add(Number("SCU", 48));
        _grid.Columns.Add(Number("Outlay", 85));
        _grid.Columns.Add(Number("Profit", 85));
        _grid.Columns.Add(Number("Dist. (Gm)", 80, "Quantum travel distance in gigametres; 0 is the same planet or moon."));
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Crates", Width = 280, ToolTipText = "What to ask for, and whether it was placed on the ship's real cargo grid." });
        _grid.Columns.Add(Number("Age (h)", 60, "Hours since the older of the two prices was reported to UEX or seen by you."));
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source", Width = 126, ToolTipText = "Where the buy and the sell price come from. Hover over a price for the details." });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Confidence", Width = 84, ToolTipText = "High: your own price from the last 12 hours. Medium: your own within the trusted period, or a UEX report from the last day. Low: a UEX report up to a week old. Stale: older than that. The weaker of the two prices decides." });

        _mode.Items.AddRange(["UEX + my prices", "UEX only", "My prices only"]);
        _mode.SelectedIndex = store is null ? (int)PriceMode.UexOnly : (int)host.Settings.PriceMode;
        _mode.Enabled = _quickReport.Enabled = store is not null;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 6, 10, 6), BackColor = Color.White, WrapContents = false };
        footer.Controls.AddRange([_otherSystems, new Label { Text = "Prices", AutoSize = true, Margin = new Padding(3, 8, 3, 0) }, _mode, _quickReport, _sendTablet, _status]);

        var ideas = new TabPage("Trade ideas");
        ideas.Controls.Add(_grid);
        ideas.Controls.Add(left);
        ideas.Controls.Add(footer);
        _tabs.TabPages.Add(ideas);
        if (store is not null)
        {
            _reporter = new MarketReporterPanel(_context);
            AddTab("Market reporter", _reporter);
            AddTab("Trading journal", new TradingJournalPanel(_context));
            AddTab("Market history", new MarketHistoryPanel(_context));
            AddTab("Settings", new TradeSettingsPanel(_context));
        }
        else _status.Text = $"The local market database could not be opened, so only UEX prices are available: {storeProblem}";

        Controls.Add(_tabs);
        Controls.Add(header);
        ShowHeader();

        _tabs.SelectedIndexChanged += (_, _) => ShowHeader();
        _search.TextChanged += (_, _) => ShowStations();
        _stations.SelectedIndexChanged += async (_, _) => await LoadRoutesAsync();
        _otherSystems.CheckedChanged += (_, _) => ShowRoutes();
        _mode.SelectedIndexChanged += (_, _) =>
        {
            if (store is null || host.Settings.PriceMode == (PriceMode)_mode.SelectedIndex) return;
            host.Settings.PriceMode = (PriceMode)_mode.SelectedIndex;
            host.SaveSettings();
            _context.NotifyChanged();
        };
        _quickReport.Click += async (_, _) =>
        {
            if (_reporter is null) return;
            _tabs.SelectedIndex = 1;
            await _reporter.QuickReportAsync(_stations.SelectedItem as UexTerminal);
        };
        _grid.SelectionChanged += (_, _) => _sendTablet.Enabled = SelectedIdea is not null;
        _sendTablet.Click += async (_, _) => await SendToTabletAsync();
        // Saved observations and changed settings show up in the runs straight away.
        _context.Changed += () =>
        {
            if (store is not null && _mode.SelectedIndex != (int)host.Settings.PriceMode) _mode.SelectedIndex = (int)host.Settings.PriceMode;
            ShowRoutes();
        };
        Shown += async (_, _) => await LoadStationsAsync();
    }

    private void AddTab(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        _tabs.TabPages.Add(page);
    }

    private TradeIdea? SelectedIdea => _grid.SelectedRows.Count == 1 ? _grid.SelectedRows[0].Tag as TradeIdea : null;

    // Starts the selected run in the Windows monitor and also puts it on the tablet when one is linked.
    private async Task SendToTabletAsync()
    {
        if (SelectedIdea is not { } idea || _origin is not { } origin) return;
        var route = idea.Route;
        var destination = _terminals.FirstOrDefault(x => x.Id == route.IdTerminalDestination);
        var run = new TradeRun(_ship.Name, origin, route.IdTerminalDestination, destination?.Label ?? route.DestinationTerminalName ?? "Unknown",
            destination?.StarSystemName ?? route.DestinationStarSystemName ?? "", route.IdCommodity, route.CommodityName ?? "Unknown",
            idea.Load.Scu, idea.Buy.Price, idea.Sell.Price, idea.Load.Crates, route.Distance, SourceOf(idea),
            PriceSelector.Rate(idea.Buy, idea.Sell, DateTime.UtcNow).ToString(), DateTime.UtcNow, destination?.FullName ?? "");
        _sendTablet.Enabled = false;
        _status.Text = "Starting the run monitor…";
        try { _status.Text = await _context.Host.SendRunToTablet(run); }
        finally { _sendTablet.Enabled = SelectedIdea is not null; }
    }

    // Called by the main window when a step was ticked on the tablet: the journal has changed, and here is the news.
    public void ShowTabletRun(string message)
    {
        _context.NotifyChanged();
        _status.Text = message;
    }

    private static string SourceOf(TradeIdea idea) =>
        idea.Buy.IsLocal == idea.Sell.IsLocal ? idea.Buy.IsLocal ? "Mine" : "UEX" : idea.Buy.IsLocal ? "Mine buy · UEX sell" : "UEX buy · mine sell";

    private void ShowHeader() => (_title.Text, _subtitle.Text) = _headers[Math.Max(0, _tabs.SelectedIndex)];

    private static DataGridViewTextBoxColumn Number(string header, int width, string toolTip = "") => new()
    {
        HeaderText = header, Width = width, ValueType = typeof(double), ToolTipText = toolTip,
        DefaultCellStyle = new DataGridViewCellStyle { Format = "N0", Alignment = DataGridViewContentAlignment.MiddleRight }
    };

    private async Task LoadStationsAsync()
    {
        try
        {
            var terminals = await _context.Uex.CommodityTerminalsAsync(CancellationToken.None);
            var commodities = await _context.Uex.CommoditiesAsync(CancellationToken.None);
            // The price list only adds report dates and the reporter's comparison; routes work without it.
            UexData<UexPrice>? prices = null;
            try { prices = await _context.Uex.PricesAsync(CancellationToken.None); }
            catch (UexException) { }
            _terminals = terminals.Rows.Where(x => x.IsAvailableLive == 1).OrderBy(x => x.ToString(), StringComparer.OrdinalIgnoreCase).ToList();
            _illegal = commodities.Rows.Where(x => x.IsIllegal == 1).Select(x => x.Id).ToHashSet();
            if (_context.HasStore) _status.Text = "Pick the station you are at.";
            _context.Load(_terminals, commodities.Rows, prices);
            ShowStations();
            if (_terminals.FirstOrDefault(x => x.ToString() == Station) is { } last) _stations.SelectedItem = last;
            else _search.Focus();
        }
        catch (UexException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void ShowStations()
    {
        var selected = _stations.SelectedItem;
        var words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Squash).ToArray();
        _stations.BeginUpdate();
        _stations.Items.Clear();
        _stations.Items.AddRange(_terminals.Where(x => words.All(word => Squash(x.ToString()).Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray<object>());
        // Keeps the routes on screen while the search box is edited.
        if (selected is not null && _stations.Items.Contains(selected)) _stations.SelectedItem = selected;
        _stations.EndUpdate();
    }

    // "area18" finds "TDD Area 18".
    private static string Squash(string value) => string.Concat(value.Where(char.IsLetterOrDigit));

    private async Task LoadRoutesAsync()
    {
        if (_stations.SelectedItem is not UexTerminal terminal || terminal.ToString() == Station && _routes is not null) return;
        Station = terminal.ToString();
        var request = ++_request;
        _routes = null;
        _origin = terminal;
        _grid.Rows.Clear();
        _status.Text = $"Asking UEX what sells from {terminal.Label}…";
        try
        {
            var routes = await _context.Uex.RoutesFromAsync(terminal.Id, CancellationToken.None);
            if (request != _request) return;
            _routes = routes;
            ShowRoutes();
        }
        catch (UexException ex)
        {
            if (request == _request) _status.Text = ex.Message;
        }
    }

    private void ShowRoutes()
    {
        if (_routes is not { } routes || _origin is not { } origin) return;
        var names = _terminals.ToDictionary(x => x.Id);
        var now = DateTime.UtcNow;
        var mode = (PriceMode)_mode.SelectedIndex;
        List<MarketObservation> seen = [];
        string? problem = null;
        if (mode != PriceMode.UexOnly && _context.HasStore)
        {
            try { seen = _context.Store.Observations(); }
            catch (Microsoft.Data.Sqlite.SqliteException ex) { problem = $" Your own prices could not be read ({ex.Message}), so these are UEX's alone."; }
        }
        var local = new LocalPrices(seen);
        var (ideas, elsewhere) = TradeIdeaBuilder.Build(origin, routes.Rows, _ship, _otherSystems.Checked, mode, local, _context.Price, now,
            TimeSpan.FromHours(_context.Settings.LocalPriceMaxAgeHours));

        _grid.Rows.Clear();
        foreach (var idea in ideas)
        {
            var (route, buy, sell, load, profit, sizesKnown) = idea;
            var commodity = (_illegal.Contains(route.IdCommodity) ? "Illegal — " : "") + (route.CommodityName ?? "Unknown");
            var sameSystem = string.Equals(route.OriginStarSystemName, route.DestinationStarSystemName, StringComparison.OrdinalIgnoreCase);
            var destination = !names.TryGetValue(route.IdTerminalDestination, out var terminal) ? route.DestinationTerminalName ?? "Unknown"
                : sameSystem && !string.IsNullOrWhiteSpace(terminal.PlanetName) ? $"{terminal.Label} — {terminal.PlanetName}" : terminal.ToString();
            var oldest = buy.AsOfUtc < sell.AsOfUtc ? buy.AsOfUtc : sell.AsOfUtc;
            var confidence = PriceSelector.Rate(buy, sell, now);
            var row = _grid.Rows[_grid.Rows.Add(commodity, destination, buy.Price, sell.Price, (double)load.Scu, load.Scu * buy.Price, profit, route.Distance,
                (load.Basis == CrateBasis.Grid ? load.Crates : $"{load.Crates} — theoretical") + (sizesKnown ? "" : " (sizes unknown)"),
                buy.AsOfUtc is null || sell.AsOfUtc is null ? null : (double?)Math.Max(0, Math.Round((now - oldest!.Value).TotalHours)),
                SourceOf(idea), confidence.ToString())];
            row.Tag = idea;
            if (terminal is { FullName.Length: > 0 })
                row.Cells[SellAtColumn].ToolTipText = $"{terminal.FullName} ({terminal.Region}). A city has several commodity terminals; this is the one that buys it.";
            if (_illegal.Contains(route.IdCommodity)) row.DefaultCellStyle.ForeColor = Color.Firebrick;
            Explain(row.Cells[BuyColumn], buy, _context.UexQuote(origin.Id, route.IdCommodity, buy: true), now);
            Explain(row.Cells[SellColumn], sell, _context.UexQuote(route.IdTerminalDestination, route.IdCommodity, buy: false), now);
            row.Cells[CratesColumn].ToolTipText = (load.Basis == CrateBasis.Grid
                    ? $"Every crate was placed on the {_ship.Name}'s cargo grid as listed in ships.json, snapped to the grid and resting on the deck or on other crates."
                    : $"No cargo grid is listed for the {_ship.Name} in ships.json, so only its {_ship.CapacityScu:N0} SCU capacity was used. Whether these crates physically fit has not been checked.") +
                (sizesKnown ? "" : " Neither UEX nor you have the crate sizes for this commodity at one of the two terminals, so every size was assumed to be on offer.");
            row.Cells[ConfidenceColumn].Style.ForeColor = confidence switch
            {
                Confidence.High => Color.FromArgb(23, 128, 93),
                Confidence.Stale or Confidence.Unknown => Color.Firebrick,
                _ => row.DefaultCellStyle.ForeColor
            };
        }
        _grid.ClearSelection();

        var hidden = elsewhere > 0 ? $" {elsewhere:N0} more sell in another star system." : "";
        var version = routes.Rows.Select(x => x.GameVersionOrigin).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var mine = ideas.Count(x => x.Buy.IsLocal || x.Sell.IsLocal);
        _status.Text = ideas.Count == 0
            ? mode == PriceMode.LocalOnly
                ? $"You have no confirmed buy price at {Station} with a confirmed sell price elsewhere yet.{hidden}"
                : $"UEX knows nothing to buy at {Station} that sells at a profit.{hidden}"
            : $"{ideas.Count:N0} run(s), best profit first.{hidden}" + (mine > 0 ? $" {mine:N0} use your own prices (green)." : "") +
              $" UEX data fetched {routes.FetchedUtc.ToLocalTime():HH:mm}" + (version is null ? "." : $", game version {version}.");
        _status.Text += problem;
    }

    // Says on hover where a price comes from, and what UEX has when the player's own price is shown instead.
    private static void Explain(DataGridViewCell cell, PriceQuote quote, PriceQuote? uex, DateTime now)
    {
        var age = TradeUi.Age(quote.AsOfUtc, now);
        if (!quote.IsLocal)
        {
            cell.ToolTipText = age.Length > 0 ? $"Reported to UEX {age} ago." : "UEX report; its date could not be loaded.";
            return;
        }
        cell.Style.BackColor = TradeUi.Mine;
        cell.ToolTipText = $"Your own {(quote.Source == PriceSource.Manual ? "entry" : "screenshot")}, {age} ago" +
                           (quote.Stale ? ", which is older than you trust your own prices for." : ".") +
                           (uex is null ? " UEX has no price here." : $" UEX has {uex.Price:N0}" + (TradeUi.Age(uex.AsOfUtc, now) is { Length: > 0 } reported ? $", reported {reported} ago." : "."));
    }
}
