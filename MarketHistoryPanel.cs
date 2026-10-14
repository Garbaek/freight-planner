namespace FreightCopilot;

// Every stored observation, newest first, with how each price moved against the one before it.
public sealed class MarketHistoryPanel : UserControl
{
    private const string AllTerminals = "All terminals", AllCommodities = "All commodities";
    private const int ChangeColumn = 5;

    private readonly TradeContext _context;
    private readonly DataGridView _grid = TradeUi.Grid();
    private readonly ComboBox _terminal = Filter(260);
    private readonly ComboBox _commodity = Filter(200);
    private readonly ComboBox _source = Filter(150);
    private readonly Button _confirm = TradeUi.Button("Confirm / unconfirm");
    private readonly Button _delete = TradeUi.Button("Delete");
    private readonly Button _submit = TradeUi.Button("Submit to UEX…");
    private readonly Button _open = TradeUi.Button("Open screenshot");
    private readonly Label _status = TradeUi.Caption("");
    private bool _filling;

    public MarketHistoryPanel(TradeContext context)
    {
        _context = context;
        Dock = DockStyle.Fill;
        _grid.MultiSelect = true;
        _grid.Columns.Add(TradeUi.Text("Seen", 104));
        _grid.Columns.Add(TradeUi.Text("Terminal", 190));
        _grid.Columns.Add(TradeUi.Fill("Commodity", 150));
        _grid.Columns.Add(TradeUi.Text("Side", 76));
        _grid.Columns.Add(TradeUi.Number("Price / SCU", 88, format: "#,0.##"));
        _grid.Columns.Add(TradeUi.Text("Change", 150, "Against the observation before it for the same commodity, terminal and side, whoever reported it."));
        _grid.Columns.Add(TradeUi.Number("Stock SCU", 78));
        _grid.Columns.Add(TradeUi.Number("Demand SCU", 86));
        _grid.Columns.Add(TradeUi.Text("Status", 100));
        _grid.Columns.Add(TradeUi.Text("Crates", 110));
        _grid.Columns.Add(TradeUi.Text("Source", 124));
        _grid.Columns.Add(TradeUi.Text("Confirmed", 76));
        _grid.Columns.Add(TradeUi.Text("UEX", 170, "Whether this observation was sent to UEX."));
        _source.Items.AddRange(["All sources", "Mine", "UEX"]);
        _source.SelectedIndex = 0;

        Controls.Add(_grid);
        Controls.Add(TradeUi.Bar(DockStyle.Bottom, _status));
        Controls.Add(TradeUi.Bar(DockStyle.Top, _terminal, _commodity, _source, _confirm, _delete, _submit, _open));

        foreach (var filter in new[] { _terminal, _commodity, _source }) filter.SelectedIndexChanged += (_, _) => { if (!_filling) ShowRows(); };
        _confirm.Click += (_, _) => ToggleConfirmed();
        _delete.Click += (_, _) => Delete();
        _submit.Click += async (_, _) => await SubmitAsync();
        _open.Click += (_, _) => OpenScreenshot();
        _context.Changed += Reload;
        Disposed += (_, _) => _context.Changed -= Reload;
        Reload();
    }

    private static ComboBox Filter(int width) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(3, 3, 10, 0), IntegralHeight = false, DropDownHeight = 420 };

    private List<MarketObservation> Selected() => _grid.SelectedRows.Cast<DataGridViewRow>().Select(x => x.Tag).OfType<MarketObservation>().ToList();

    private void Reload()
    {
        var all = _context.Store.Observations();
        _filling = true;
        Refill(_terminal, AllTerminals, all.Select(x => x.TerminalName));
        Refill(_commodity, AllCommodities, all.Select(x => x.CommodityName));
        _filling = false;
        ShowRows(all);
    }

    private static void Refill(ComboBox box, string everything, IEnumerable<string> names)
    {
        var selected = box.SelectedItem as string;
        box.Items.Clear();
        box.Items.Add(everything);
        box.Items.AddRange(names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray<object>());
        box.SelectedItem = selected is not null && box.Items.Contains(selected) ? selected : everything;
    }

    private void ShowRows(List<MarketObservation>? all = null)
    {
        all ??= _context.Store.Observations();
        // The observation before each one, whichever source it came from, so own prices line up against UEX's.
        var previous = new Dictionary<long, MarketObservation>();
        foreach (var series in all.GroupBy(x => (x.TerminalId, x.CommodityId, x.IsBuy)))
        {
            var ordered = series.OrderBy(x => x.ObservedUtc).ThenBy(x => x.Id).ToList();
            for (var i = 1; i < ordered.Count; i++) previous[ordered[i].Id] = ordered[i - 1];
        }

        var terminal = _terminal.SelectedItem as string;
        var commodity = _commodity.SelectedItem as string;
        _grid.Rows.Clear();
        foreach (var row in all.Where(x => (terminal is null or AllTerminals || x.TerminalName == terminal) &&
                                           (commodity is null or AllCommodities || x.CommodityName == commodity) &&
                                           (_source.SelectedIndex == 0 || _source.SelectedIndex == 2 == (x.Source == PriceSource.Uex))))
        {
            var before = previous.GetValueOrDefault(row.Id);
            var change = before?.Price is > 0 && row.Price is > 0 ? (row.Price.Value - before.Price.Value) / before.Price.Value * 100 : (double?)null;
            var index = _grid.Rows.Add(TradeUi.When(row.ObservedUtc), string.IsNullOrEmpty(row.StarSystem) ? row.TerminalName : $"{row.TerminalName} — {row.StarSystem}",
                row.CommodityName, row.IsBuy ? "You buy" : "You sell", row.Price,
                change is null ? "" : $"{change:+0.0;-0.0;0.0} % (was {before!.Price:#,0.##})", (double?)row.StockScu, (double?)row.DemandScu,
                TradeUi.Status(row.Status), row.ContainerSizes, TradeUi.Source(row.Source), row.Source == PriceSource.Uex ? "" : row.Verified ? "Yes" : "No",
                row.Submission switch
                {
                    SubmissionState.Submitted => $"Sent {TradeUi.When(row.SubmittedUtc ?? row.ObservedUtc)}",
                    SubmissionState.Test => "Test accepted",
                    SubmissionState.Failed => $"Refused: {row.SubmissionDetail}",
                    _ => ""
                });
            var gridRow = _grid.Rows[index];
            gridRow.Tag = row;
            gridRow.Cells[ChangeColumn].ToolTipText = before is null ? "" : $"Before: {before.Price:#,0.##} on {TradeUi.When(before.ObservedUtc)}, {TradeUi.Source(before.Source)}.";
            if (change is not null and not 0) gridRow.Cells[ChangeColumn].Style.ForeColor = change > 0 ? Color.FromArgb(23, 128, 93) : Color.Firebrick;
            if (row.Source == PriceSource.Uex) gridRow.DefaultCellStyle.ForeColor = Color.DimGray;
            else if (row.Verified) gridRow.DefaultCellStyle.BackColor = TradeUi.Mine;
        }
        _grid.ClearSelection();
        _status.Text = all.Count == 0
            ? "No observations yet. Save some on the Market reporter tab."
            : $"{_grid.Rows.Count:N0} of {all.Count:N0} observation(s). Green rows are your own confirmed prices; grey rows are what UEX had at the time.";
    }

    private void ToggleConfirmed()
    {
        var mine = Selected().Where(x => x.Source != PriceSource.Uex).ToList();
        if (mine.Count == 0)
        {
            _status.Text = "Select one or more of your own observations first.";
            return;
        }
        // Confirming a row that could never be valid would let it into route calculations.
        var broken = mine.Where(x => !x.Verified && ObservationValidator.Errors(x, DateTime.UtcNow).Count > 0).ToList();
        foreach (var row in mine.Except(broken)) _context.Store.SetVerified(row.Id, !row.Verified);
        _context.NotifyChanged();
        if (broken.Count > 0) _status.Text = $"{broken.Count:N0} observation(s) are incomplete and cannot be confirmed.";
    }

    private void Delete()
    {
        var rows = Selected();
        if (rows.Count == 0 || MessageBox.Show(this, $"Delete {rows.Count:N0} observation(s) from the local history? This cannot be undone.", "Market history",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        foreach (var row in rows) _context.Store.DeleteObservation(row.Id);
        _context.NotifyChanged();
    }

    private async Task SubmitAsync()
    {
        var rows = Selected().Where(x => x.Submission != SubmissionState.Submitted).ToList();
        if (rows.Count == 0)
        {
            _status.Text = "Select your own confirmed observations that are not on UEX yet.";
            return;
        }
        _submit.Enabled = false;
        string result;
        try { result = await UexSubmitFlow.RunAsync(this, _context, rows); }
        finally { _submit.Enabled = true; }
        _status.Text = result;
    }

    private void OpenScreenshot()
    {
        var path = Selected().Select(x => x.ScreenshotPath).FirstOrDefault(File.Exists);
        if (path is null) _status.Text = "The selected observation has no screenshot on this PC.";
        else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }
}
