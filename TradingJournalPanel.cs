namespace FreightCopilot;

// The runs the player actually flew. Profit here is realized: it comes from the prices paid and received,
// never from a route estimate.
public sealed class TradingJournalPanel : UserControl
{
    private const int TimeColumn = 12;
    private readonly TradeContext _context;
    private readonly DataGridView _grid = TradeUi.Grid();
    private readonly DataGridView _groups = TradeUi.Grid();
    private readonly ComboBox _groupBy = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Margin = new Padding(3, 3, 10, 0) };
    private readonly Label _totals = new() { Dock = DockStyle.Left, Width = 400, Padding = new Padding(12, 10, 6, 6), BackColor = Color.White };
    private readonly Button _add = TradeUi.Button("New run…");
    private readonly Button _edit = TradeUi.Button("Edit / complete…");
    private readonly Button _delete = TradeUi.Button("Delete");
    private TradeStatistics _statistics = TradeStatistics.From([]);

    public TradingJournalPanel(TradeContext context)
    {
        _context = context;
        Dock = DockStyle.Fill;

        _grid.Columns.Add(TradeUi.Text("Bought", 104));
        _grid.Columns.Add(TradeUi.Text("Ship", 150));
        _grid.Columns.Add(TradeUi.Text("Commodity", 130));
        _grid.Columns.Add(TradeUi.Fill("Route", 200));
        _grid.Columns.Add(TradeUi.Number("SCU", 50));
        _grid.Columns.Add(TradeUi.Number("Buy / SCU", 82, format: "#,0.##"));
        _grid.Columns.Add(TradeUi.Number("Sell / SCU", 82, format: "#,0.##"));
        _grid.Columns.Add(TradeUi.Number("Invested", 86));
        _grid.Columns.Add(TradeUi.Number("Costs", 66, "Operating costs: auto-loading, fuel, repairs, fines."));
        _grid.Columns.Add(TradeUi.Number("Gross", 80, "Sale minus purchase."));
        _grid.Columns.Add(TradeUi.Number("Net", 80, "Gross profit minus operating costs."));
        _grid.Columns.Add(TradeUi.Number("ROI %", 62, "Net profit as a percentage of the money invested in cargo.", "N2"));
        _grid.Columns.Add(TradeUi.Text("Time", 62, "Loading, travel and unloading together. Hover over a run flown with the tablet checklist to see its steps."));
        _grid.Columns.Add(TradeUi.Number("Per hour", 84, "Net profit per hour of the recorded time."));
        _grid.Columns.Add(TradeUi.Text("State", 70));
        _grid.Columns.Add(TradeUi.Text("UEX history", 150, "The purchase and sale rows added to your personal UEX trade history."));
        _grid.Columns.Add(TradeUi.Text("Website", 125, "Whether this completed run was stored in the opt-in community trade tracker."));

        _groups.Columns.Add(TradeUi.Fill("", 180));
        _groups.Columns.Add(TradeUi.Number("Runs", 60));
        _groups.Columns.Add(TradeUi.Number("SCU", 70));
        _groups.Columns.Add(TradeUi.Number("Net profit", 100));
        _groups.Columns.Add(TradeUi.Number("Per hour", 100, "Only runs with a recorded time count."));
        _groupBy.Items.AddRange(["Profit by commodity", "Profit by route", "Best destinations"]);
        _groupBy.SelectedIndex = 0;

        var statistics = new Panel { Dock = DockStyle.Fill };
        statistics.Controls.Add(_groups);
        statistics.Controls.Add(_totals);
        var split = TradeUi.Split(Orientation.Horizontal, secondPanel: 220);
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(statistics);

        Controls.Add(split);
        Controls.Add(TradeUi.Bar(DockStyle.Top, _add, _edit, _delete, TradeUi.Caption("Statistics"), _groupBy));

        _add.Click += (_, _) => Edit(new TradeRecord { Ship = _context.Host.Ship().Name, BoughtUtc = DateTime.UtcNow });
        _edit.Click += (_, _) => { if (Selected is { } trade) Edit(trade); };
        _delete.Click += (_, _) => Delete();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && Selected is { } trade) Edit(trade); };
        _groupBy.SelectedIndexChanged += (_, _) => ShowGroups();
        _context.Changed += ShowTrades;
        Disposed += (_, _) => _context.Changed -= ShowTrades;
        ShowTrades();
    }

    // The steps ticked on the tablet, one per line, for a run flown with its checklist.
    private static string Steps(TradeRecord trade)
    {
        try
        {
            return trade.StepTimes.Length == 0 ? "" : string.Join(Environment.NewLine, System.Text.Json.JsonSerializer.Deserialize<List<StepTime>>(trade.StepTimes)!
                .Select(x => $"{x.Utc.ToLocalTime():HH:mm:ss}  {x.Label}  ({TradeUi.Duration(Math.Max(1, x.Seconds))})"));
        }
        catch (System.Text.Json.JsonException) { return ""; }
    }

    private TradeRecord? Selected => _grid.CurrentRow?.Tag as TradeRecord;

    private void Edit(TradeRecord trade)
    {
        using var editor = new TradeEditorForm(_context, trade);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _context.Store.Save(editor.Trade);
        _context.NotifyChanged();
    }

    private void Delete()
    {
        if (Selected is not { } trade || MessageBox.Show(this, $"Delete the run {trade.CommodityName}, {trade.Route}?", "Trading journal",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _context.Store.DeleteTrade(trade.Id);
        _context.NotifyChanged();
    }

    private void ShowTrades()
    {
        var trades = _context.Store.Trades();
        _grid.Rows.Clear();
        foreach (var trade in trades)
        {
            var investment = TradeMath.Investment(trade.Scu, trade.PriceBuy);
            var net = TradeMath.Net(trade);
            var seconds = TradeMath.TotalSeconds(trade);
            var index = _grid.Rows.Add(TradeUi.When(trade.BoughtUtc), trade.Ship, trade.CommodityName,
                trade.DestinationName.Length > 0 ? trade.Route : $"{trade.OriginName} → not sold yet", (double)trade.Scu, trade.PriceBuy, trade.PriceSell,
                investment, trade.OperatingCosts, TradeMath.Gross(trade), net, net is null ? null : TradeMath.Roi(net.Value, investment),
                TradeUi.Duration(seconds), net is null ? null : TradeMath.ProfitPerHour(net.Value, seconds),
                trade.IsCompleted ? "Sold" : trade.IsPartiallySold ? $"Partial {trade.SoldScu:N0}/{trade.Scu:N0}" : "Open", UexState(trade), TrackerState(trade));
            _grid.Rows[index].Tag = trade;
            _grid.Rows[index].Cells[TimeColumn].ToolTipText = Steps(trade);
            // An open run has cost money and earned nothing yet.
            if (!trade.IsCompleted) _grid.Rows[index].DefaultCellStyle.BackColor = Color.LemonChiffon;
            else if (net < 0) _grid.Rows[index].Cells[10].Style.ForeColor = Color.Firebrick;
        }
        _grid.ClearSelection();

        _statistics = TradeStatistics.From(trades);
        _totals.Text = _statistics.Runs + _statistics.OpenRuns == 0
            ? "No runs yet. Add one with New run, or use Log trade on the Market reporter tab after buying or selling."
            : string.Join(Environment.NewLine,
                $"Completed runs: {_statistics.Runs:N0}" + (_statistics.OpenRuns > 0 ? $"  (and {_statistics.OpenRuns:N0} open)" : ""),
                $"Cargo moved: {_statistics.Scu:N0} SCU",
                $"Realized net profit: {_statistics.NetProfit:N0} aUEC",
                $"Gross profit: {_statistics.GrossProfit:N0} aUEC",
                $"Return on cargo money: {(TradeMath.Roi(_statistics.NetProfit, _statistics.Investment) is { } roi ? $"{roi:N2} %" : "—")}",
                $"Profit per hour: {(_statistics.ProfitPerHour is { } rate ? $"{rate:N0} aUEC" : "no times recorded")}",
                "",
                "Only sold cargo counts. Route estimates from Trade ideas are never added here.");
        ShowGroups();
    }

    private static string UexState(TradeRecord trade)
    {
        static string Side(string name, SubmissionState state, long? id, string detail) => state switch
        {
            SubmissionState.Submitted => $"{name} ✓" + (id is { } value ? $" #{value}" : ""),
            SubmissionState.Failed => $"{name} failed" + (detail.Length > 0 ? $": {detail}" : ""),
            _ => $"{name} —"
        };
        var sales = trade.Sales.Count == 0
            ? Side("Sell", trade.UexSellSubmission, trade.UexSellId, trade.UexSellDetail)
            : $"Sales {trade.Sales.Count(x => x.UexSubmission == SubmissionState.Submitted)}/{trade.Sales.Count} ✓" +
              (trade.Sales.Any(x => x.UexSubmission == SubmissionState.Failed) ? " (retry needed)" : "");
        return Side("Buy", trade.UexBuySubmission, trade.UexBuyId, trade.UexBuyDetail) + "  ·  " + sales;
    }

    private static string TrackerState(TradeRecord trade) => trade.TrackerSubmission switch
    {
        SubmissionState.Submitted => "Contributed ✓",
        SubmissionState.Failed => "Failed: " + trade.TrackerDetail,
        _ => "Local only"
    };

    private void ShowGroups()
    {
        var groups = _groupBy.SelectedIndex switch { 1 => _statistics.ByRoute, 2 => _statistics.ByDestination, _ => _statistics.ByCommodity };
        _groups.Columns[0].HeaderText = _groupBy.SelectedIndex switch { 1 => "Route", 2 => "Sold at", _ => "Commodity" };
        _groups.Rows.Clear();
        foreach (var group in groups) _groups.Rows.Add(group.Name, (double)group.Runs, (double)group.Scu, group.Profit, group.ProfitPerHour);
        _groups.ClearSelection();
    }
}

public sealed class TradeEditorForm : Form
{
    private readonly TradeContext _context;
    private readonly ComboBox _ship = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems, IntegralHeight = false, DropDownHeight = 320 };
    private readonly ComboBox _commodity = Picker();
    private readonly ComboBox _origin = Picker();
    private readonly ComboBox _destination = Picker();
    private readonly NumericUpDown _scu = new() { Dock = DockStyle.Fill, Maximum = 100_000, ThousandsSeparator = true };
    private readonly NumericUpDown _buy = Money();
    private readonly NumericUpDown _sell = Money();
    private readonly NumericUpDown _costs = Money();
    private readonly DateTimePicker _bought = Time(checkBox: false);
    private readonly DateTimePicker _sold = Time(checkBox: true);
    private readonly TextBox _loading = new() { Dock = DockStyle.Fill, PlaceholderText = "min:sec, e.g. 2:12" };
    private readonly TextBox _travel = new() { Dock = DockStyle.Fill, PlaceholderText = "min:sec, or minutes" };
    private readonly TextBox _unloading = new() { Dock = DockStyle.Fill, PlaceholderText = "min:sec" };
    private readonly TextBox _notes = new() { Dock = DockStyle.Fill };
    private readonly Label _summary = new() { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 0), Font = new Font("Segoe UI Semibold", 10) };
    private readonly Label _hint = new() { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0), ForeColor = Color.DimGray };

    public TradeRecord Trade { get; }

    public TradeEditorForm(TradeContext context, TradeRecord trade)
    {
        _context = context;
        Trade = trade;
        Text = "Freight Planner — Trading Journal Entry";
        Width = 900;
        Height = 560;
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = TradeUi.Dark };
        header.Controls.Add(new Label { Text = trade.Id > 0 ? "EDIT RUN" : "NEW RUN", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 18), AutoSize = true, Location = new Point(20, 11) });
        header.Controls.Add(new Label { Text = "What you really paid and received. Leave the sell price at 0 while the cargo is still on board.", ForeColor = TradeUi.Muted, AutoSize = true, Location = new Point(22, 44) });

        var prices = TradeUi.Button("Use my observed prices");
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, Height = 300, ColumnCount = 4, Padding = new Padding(8) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddField(fields, 0, "Ship", _ship, "Commodity", _commodity);
        AddField(fields, 1, "Bought at", _origin, "Sold at", _destination);
        AddField(fields, 2, "Cargo SCU", _scu, "Operating costs", _costs);
        AddField(fields, 3, "Buy price / SCU", _buy, "Sell price / SCU", _sell);
        AddField(fields, 4, "Bought on", _bought, "Sold on", _sold);
        AddField(fields, 5, "Loading time", _loading, "Unloading time", _unloading);
        AddField(fields, 6, "Travel time", _travel, "Notes", _notes);

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "Save run", AutoSize = true };
        save.Click += (_, _) => Save();
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = Color.White };
        footer.Controls.AddRange([cancel, save, prices]);

        var texts = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        texts.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        texts.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        texts.Controls.Add(_summary, 0, 0);
        texts.Controls.Add(_hint, 0, 1);

        Controls.Add(texts);
        Controls.Add(fields);
        Controls.Add(footer);
        Controls.Add(header);
        AcceptButton = save;
        CancelButton = cancel;

        _ship.Items.AddRange(context.Host.Ships.Select(x => x.Name).ToArray<object>());
        _ship.Text = trade.Ship;
        TradeUi.Fill(_commodity, context.Commodities, x => x.Id == trade.CommodityId);
        TradeUi.Fill(_origin, context.Terminals, x => x.Id == trade.OriginId);
        TradeUi.Fill(_destination, context.Terminals, x => x.Id == trade.DestinationId);
        _scu.Value = Math.Clamp(trade.Scu, 0, _scu.Maximum);
        _buy.Value = Limit(trade.PriceBuy, _buy);
        _sell.Value = Limit(trade.PriceSell ?? 0, _sell);
        _costs.Value = Limit(trade.OperatingCosts ?? 0, _costs);
        _bought.Value = trade.BoughtUtc == default ? DateTime.Now : trade.BoughtUtc.ToLocalTime();
        _sold.Value = (trade.SoldUtc ?? DateTime.UtcNow).ToLocalTime();
        _sold.Checked = trade.SoldUtc is not null;
        _loading.Text = TradeUi.Duration(trade.LoadingSeconds);
        _travel.Text = TradeUi.Duration(trade.TravelSeconds);
        _unloading.Text = TradeUi.Duration(trade.UnloadingSeconds);
        _notes.Text = trade.Notes;
        if (trade.IsPartiallySold || trade.Sales.Count > 1)
        {
            _scu.Enabled = _sell.Enabled = _sold.Enabled = _destination.Enabled = false;
            _hint.Text = $"This cargo lot has {trade.Sales.Count:N0} recorded sale transaction(s), {trade.SoldScu:N0} SCU sold and {trade.RemainingScu:N0} SCU remaining. Sale legs are protected so their UEX history stays intact.";
        }

        prices.Click += (_, _) => UseObservedPrices(overwrite: true);
        foreach (var box in new[] { _commodity, _origin, _destination }) box.SelectedIndexChanged += (_, _) => { UseObservedPrices(overwrite: false); ShowSummary(); };
        foreach (var number in new[] { _scu, _buy, _sell, _costs }) number.ValueChanged += (_, _) => ShowSummary();
        foreach (var text in new[] { _loading, _travel, _unloading }) text.TextChanged += (_, _) => ShowSummary();
        // Typing a sell price means the cargo is sold.
        _sell.ValueChanged += (_, _) => { if (_sell.Value > 0) _sold.Checked = true; };
        UseObservedPrices(overwrite: false);
        ShowSummary();
        if (trade.IsPartiallySold || trade.Sales.Count > 1)
            _hint.Text = $"This cargo lot has {trade.Sales.Count:N0} recorded sale transaction(s), {trade.SoldScu:N0} SCU sold and {trade.RemainingScu:N0} SCU remaining. Sale legs are protected so their UEX history stays intact.";
    }

    private static ComboBox Picker() => new()
    {
        Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems, IntegralHeight = false, DropDownHeight = 320
    };

    private static NumericUpDown Money() => new() { Dock = DockStyle.Fill, Maximum = 100_000_000, DecimalPlaces = 2, ThousandsSeparator = true };

    private static DateTimePicker Time(bool checkBox) => new() { Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom, CustomFormat = "d MMM yyyy HH:mm", ShowCheckBox = checkBox };

    private static decimal Limit(double value, NumericUpDown box) => Math.Clamp((decimal)Math.Max(0, value), box.Minimum, box.Maximum);

    private static void AddField(TableLayoutPanel panel, int row, string label1, Control control1, string label2, Control control2)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        panel.Controls.Add(new Label { Text = label1, Anchor = AnchorStyles.Left, AutoSize = true }, 0, row);
        panel.Controls.Add(control1, 1, row);
        panel.Controls.Add(new Label { Text = label2, Anchor = AnchorStyles.Left, AutoSize = true }, 2, row);
        panel.Controls.Add(control2, 3, row);
    }

    // Fills empty price boxes from the player's newest confirmed observations, and says where each came from.
    private void UseObservedPrices(bool overwrite)
    {
        if (_commodity.SelectedItem is not UexCommodity commodity) return;
        var local = new LocalPrices(_context.Store.Observations(commodityId: commodity.Id));
        var found = new List<string>();
        Use(_origin, _buy, buy: true, "Buy");
        Use(_destination, _sell, buy: false, "Sell");
        if (found.Count > 0) _hint.Text = string.Join(Environment.NewLine, found) + Environment.NewLine + "Change them if you paid or received something else.";
        else if (overwrite) _hint.Text = "No confirmed observation of this commodity at these terminals yet.";

        void Use(ComboBox place, NumericUpDown box, bool buy, string side)
        {
            if (place.SelectedItem is not UexTerminal terminal || local.Find(terminal.Id, commodity.Id, buy) is not { } seen || !overwrite && box.Value > 0) return;
            box.Value = Limit(seen.Price!.Value, box);
            found.Add($"{side} price {seen.Price:#,0.##} is your {(seen.Source == PriceSource.Manual ? "own entry" : "screenshot")} at {terminal.Label} of {TradeUi.When(seen.ObservedUtc)}.");
        }
    }

    private void ShowSummary()
    {
        var scu = (int)_scu.Value;
        var investment = TradeMath.Investment(scu, (double)_buy.Value);
        if (_sell.Value <= 0)
        {
            _summary.Text = $"Not sold yet: {investment:N0} aUEC is tied up in cargo.";
            return;
        }
        var net = TradeMath.NetProfit(scu, (double)_buy.Value, (double)_sell.Value, (double)_costs.Value);
        var times = new[] { _loading, _travel, _unloading }.Select(x => TradeUi.ParseDuration(x.Text)).ToList();
        var seconds = times.Any(x => x is > 0) ? times.Sum(x => Math.Max(0, x ?? 0)) : (int?)null;
        _summary.Text = $"Invested {investment:N0}  ·  Sold for {TradeMath.Revenue(scu, (double)_sell.Value):N0}  ·  Gross {TradeMath.GrossProfit(scu, (double)_buy.Value, (double)_sell.Value):N0}" +
                        $"  ·  Net {net:N0}  ·  ROI {(TradeMath.Roi(net, investment) is { } roi ? $"{roi:N2} %" : "—")}" +
                        (TradeMath.ProfitPerHour(net, seconds) is { } rate ? $"  ·  {rate:N0} aUEC/h" : "");
        _summary.ForeColor = net < 0 ? Color.Firebrick : Color.FromArgb(23, 128, 93);
    }

    private void Save()
    {
        var times = new[] { _loading, _travel, _unloading }.Select(x => TradeUi.ParseDuration(x.Text)).ToList();
        var problem = _commodity.SelectedItem is not UexCommodity ? "Pick the commodity from the list."
            : _origin.SelectedItem is not UexTerminal ? "Pick where the cargo was bought from the list."
            : _scu.Value <= 0 ? "Enter how many SCU were bought."
            : _buy.Value <= 0 ? "Enter the buy price per SCU."
            : _sell.Value > 0 && _destination.SelectedItem is not UexTerminal ? "Pick where the cargo was sold from the list."
            : _sell.Value <= 0 && _sold.Checked ? "Enter the sell price, or untick the sold date while the cargo is still on board."
            : times.Any(x => x < 0) ? "Write times as min:sec (2:12), h:min:sec, or plain minutes."
            : _sold.Checked && _sold.Value < _bought.Value ? "The cargo cannot be sold before it was bought."
            : null;
        if (problem is not null)
        {
            MessageBox.Show(this, problem, "Trading journal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var commodity = (UexCommodity)_commodity.SelectedItem!;
        var origin = (UexTerminal)_origin.SelectedItem!;
        var destination = _destination.SelectedItem as UexTerminal;
        Trade.Ship = _ship.Text.Trim();
        (Trade.CommodityId, Trade.CommodityName) = (commodity.Id, commodity.Name ?? "");
        (Trade.OriginId, Trade.OriginName) = (origin.Id, origin.Label);
        (Trade.DestinationId, Trade.DestinationName) = (destination?.Id ?? 0, destination?.Label ?? "");
        Trade.Scu = (int)_scu.Value;
        Trade.PriceBuy = (double)_buy.Value;
        Trade.PriceSell = _sell.Value > 0 ? (double)_sell.Value : null;
        Trade.OperatingCosts = _costs.Value > 0 ? (double)_costs.Value : null;
        (Trade.LoadingSeconds, Trade.TravelSeconds, Trade.UnloadingSeconds) = (times[0], times[1], times[2]);
        Trade.BoughtUtc = _bought.Value.ToUniversalTime();
        Trade.SoldUtc = _sell.Value > 0 ? _sold.Value.ToUniversalTime() : null;
        if (Trade.Sales.Count == 1)
        {
            var sale = Trade.Sales[0];
            sale.TerminalId = Trade.DestinationId;
            sale.TerminalName = Trade.DestinationName;
            sale.Scu = Trade.Scu;
            sale.PricePerScu = Trade.PriceSell ?? sale.PricePerScu;
            sale.SoldUtc = Trade.SoldUtc ?? sale.SoldUtc;
        }
        Trade.Notes = _notes.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}
