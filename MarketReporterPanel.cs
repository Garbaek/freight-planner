namespace FreightCopilot;

// One batch of observations at one terminal: typed in, or read from a screenshot and corrected by the player.
public sealed class MarketReporterPanel : UserControl
{
    private const int Ok = 0, Commodity = 1, Buy = 2, Sell = 3, Stock = 4, Status = 5, Demand = 6, Crates = 7, Uex = 8, UexAge = 9, Delta = 10, Check = 11;
    private const string AllSystems = "All systems";

    // What a grid row carries besides its cells.
    private sealed class RowDraft
    {
        public PriceSource Source = PriceSource.Manual;
        public List<string> Notes = [];
        // What was guessed while reading the screenshot; such a row needs its own tick.
        public List<string> Doubts = [];
        // The side the screenshot showed the price on: the lit shop tab, or the player's own goods. Null for a
        // row typed in by hand, and when the screenshot did not say.
        public bool? IsBuy;
        // Read from the player's own cargo or from a confirmation, which show a price and nothing about the shop.
        public bool PriceOnly;
        // The part of the screenshot the row was read from.
        public Rectangle? Region;
        public MarketObservation? Saved;
    }

    private readonly TradeContext _context;
    private readonly ComboBox _system = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, Margin = new Padding(3, 3, 10, 0) };
    private readonly ComboBox _terminal = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown, Width = 340, Margin = new Padding(3, 3, 10, 0), IntegralHeight = false, DropDownHeight = 420,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems
    };
    private readonly DateTimePicker _seen = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "d MMM yyyy HH:mm", Width = 160, Margin = new Padding(3, 3, 10, 0) };
    private readonly Button _newest = TradeUi.Button("Newest screenshot");
    private readonly Button _import = TradeUi.Button("Import screenshot…");
    private readonly Button _waiting = TradeUi.Button("");
    private readonly Button _addRow = TradeUi.Button("Add row");
    private readonly Button _removeRow = TradeUi.Button("Remove row");
    private readonly Button _clear = TradeUi.Button("Clear");
    private readonly Button _confirmAll = TradeUi.Button("Confirm all");
    private readonly Button _save = TradeUi.Button("Save");
    private readonly Button _submit = TradeUi.Button("Submit to UEX…");
    private readonly Button _logTrade = TradeUi.Button("Log trade…");
    private readonly PictureBox _image = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(28, 34, 41), Cursor = Cursors.Hand };
    private readonly DataGridView _grid = TradeUi.Grid(readOnly: false);
    private readonly Label _info = new() { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(6), ForeColor = Color.FromArgb(150, 45, 35), BackColor = Color.White };
    private readonly Label _status = TradeUi.Caption("Loading UEX's terminals…");
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 3000 };
    private readonly Queue<(string Path, TerminalScan Scan)> _pending = new();
    private readonly DateTime _openedUtc = DateTime.UtcNow;
    private string _screenshot = "";
    private Bitmap? _shot;
    private string _remarks = "";
    private TerminalScan? _scan;
    private bool _listsLoaded;
    private bool _loading;
    private bool _reading;

    public MarketReporterPanel(TradeContext context)
    {
        _context = context;
        Dock = DockStyle.Fill;

        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Checked", Width = 64, ToolTipText = "Tick when you have compared the row with the terminal. Only checked rows are used in Trade ideas and can go to UEX." });
        _grid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Commodity", Width = 176, FlatStyle = FlatStyle.Flat, MaxDropDownItems = 24, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton });
        _grid.Columns.Add(TradeUi.Number("Buy / SCU", 84, "What you pay per SCU at this terminal.", "#,0.##"));
        _grid.Columns.Add(TradeUi.Number("Sell / SCU", 84, "What the terminal pays you per SCU.", "#,0.##"));
        _grid.Columns.Add(Whole("Stock SCU", 76, "The shop quantity the terminal shows. Not how much you bought or sold."));
        _grid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Stock status", Width = 128, FlatStyle = FlatStyle.Flat, DataSource = TradeUi.StatusNames, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton });
        _grid.Columns.Add(Whole("Demand SCU", 86, "How much the terminal still takes, where it shows that. Kept on this PC only."));
        _grid.Columns.Add(TradeUi.Text("Crates", 108, "Crate sizes on offer, for example 1,2,4,8."));
        _grid.Columns.Add(ReadOnly(TradeUi.Number("UEX", 78, "The price UEX has for this commodity at this terminal.", "#,0.##")));
        _grid.Columns.Add(ReadOnly(TradeUi.Text("UEX age", 66, "How long ago that price was reported to UEX.")));
        _grid.Columns.Add(ReadOnly(TradeUi.Text("Δ UEX", 62, "Your price against UEX's.")));
        _grid.Columns.Add(ReadOnly(TradeUi.Fill("Check", 140)));

        var top = TradeUi.Bar(DockStyle.Top, TradeUi.Caption("System"), _system, TradeUi.Caption("Terminal"), _terminal, TradeUi.Caption("Seen at"), _seen, _newest, _import, _waiting);
        var actions = TradeUi.Bar(DockStyle.Top, _addRow, _removeRow, _clear, _confirmAll, _save, _submit, _logTrade);
        _waiting.Visible = false;

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_grid);
        right.Controls.Add(_info);
        var split = TradeUi.Split(Orientation.Vertical, firstPanel: 470);
        split.Panel1.Controls.Add(_image);
        split.Panel1.Controls.Add(new Label
        {
            Text = "Select a row to zoom in on it. Click to open the screenshot.", Dock = DockStyle.Bottom, Height = 24,
            ForeColor = TradeUi.Muted, BackColor = Color.FromArgb(28, 34, 41), Padding = new Padding(6, 4, 0, 0)
        });
        split.Panel2.Controls.Add(right);

        Controls.Add(split);
        Controls.Add(TradeUi.Bar(DockStyle.Bottom, _status));
        Controls.Add(actions);
        Controls.Add(top);

        _system.SelectedIndexChanged += (_, _) => ShowTerminals();
        _terminal.SelectedIndexChanged += (_, _) =>
        {
            // Saved rows belong to the terminal they were saved for; another terminal is another batch.
            if (!_loading && BatchIsSaved()) StartNewBatch();
            else RefreshRows();
        };
        _seen.ValueChanged += (_, _) => RefreshRows();
        _newest.Click += async (_, _) => await LoadNewestAsync(recentOnly: false);
        _import.Click += async (_, _) => await ImportAsync();
        _waiting.Click += async (_, _) => await ShowNextWaitingAsync();
        _addRow.Click += (_, _) => AddRow();
        _removeRow.Click += (_, _) => RemoveRow();
        _clear.Click += (_, _) => { if (MayReplaceRows()) StartNewBatch(); };
        _confirmAll.Click += (_, _) => ConfirmAll();
        _save.Click += (_, _) => Save();
        _submit.Click += async (_, _) => await SubmitAsync();
        _logTrade.Click += (_, _) => LogTrade();
        _image.Click += (_, _) => OpenScreenshot();
        _grid.DataError += (_, e) =>
        {
            _status.Text = e.ColumnIndex is Commodity or Status ? "Pick a value from the list." : "That is not a number. Use digits only.";
            e.ThrowException = false;
        };
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_loading || e.RowIndex < 0 || e.ColumnIndex >= Uex) return;
            var row = _grid.Rows[e.RowIndex];
            // A saved row can still be checked or unchecked; that is stored at once.
            if (e.ColumnIndex == Ok && DraftOf(row).Saved is { } saved && SetChecked([(saved, row.Cells[Ok].Value is true)])) _context.NotifyChanged();
            RefreshRow(row);
        };
        _grid.CurrentCellChanged += (_, _) => { if (!_loading) { ShowRowImage(); ShowInfo(); } };
        _watch.Tick += async (_, _) => await WatchAsync();
        _context.Changed += OnContextChanged;
        Disposed += (_, _) => { _watch.Dispose(); _context.Changed -= OnContextChanged; ShowImage(null); };
        OnContextChanged();
    }

    private UexTerminal? Terminal => _terminal.SelectedItem as UexTerminal;

    private static DataGridViewTextBoxColumn Whole(string header, int width, string toolTip)
    {
        var column = TradeUi.Number(header, width, toolTip);
        column.ValueType = typeof(int);
        return column;
    }

    private static DataGridViewTextBoxColumn ReadOnly(DataGridViewTextBoxColumn column)
    {
        column.ReadOnly = true;
        column.DefaultCellStyle.ForeColor = Color.DimGray;
        return column;
    }

    private void OnContextChanged()
    {
        if (!_listsLoaded && _context.Terminals.Count > 0)
        {
            _listsLoaded = true;
            ((DataGridViewComboBoxColumn)_grid.Columns[Commodity]).DataSource = _context.Commodities.Select(x => x.Name!).ToList();
            _system.Items.Add(AllSystems);
            _system.Items.AddRange(_context.Terminals.Select(x => x.StarSystemName ?? "").Where(x => x.Length > 0).Distinct().OrderBy(x => x).ToArray<object>());
            _system.SelectedIndex = 0;
            _status.Text = "Pick the terminal, then type the prices in or read them from a screenshot.";
        }
        _watch.Enabled = _context.Settings.WatchTerminalScreenshots;
        RefreshRows();
    }

    private void ShowTerminals()
    {
        var selected = Terminal;
        var system = _system.SelectedItem as string;
        TradeUi.Fill(_terminal, _context.Terminals.Where(x => system is null or AllSystems || x.StarSystemName == system), x => x.Id == selected?.Id);
    }

    public void SelectTerminal(UexTerminal? terminal)
    {
        if (terminal is null || Terminal?.Id == terminal.Id) return;
        if (!_terminal.Items.Contains(terminal)) _system.SelectedIndex = 0;
        _terminal.SelectedItem = terminal;
    }

    // The short way in from Trade ideas: the station is known, the newest screenshot is read straight away.
    public async Task QuickReportAsync(UexTerminal? terminal)
    {
        SelectTerminal(terminal);
        if (_pending.Count > 0) await ShowNextWaitingAsync();
        else await LoadNewestAsync(recentOnly: true);
    }

    private IEnumerable<string> Screenshots()
    {
        var folder = _context.Host.ScreenshotFolder();
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(x => x.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            : [];
    }

    private async Task LoadNewestAsync(bool recentOnly)
    {
        var newest = Screenshots().OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (newest is null)
            _status.Text = "No screenshots found. Set the screenshot folder under Settings, or use Import screenshot.";
        else if (recentOnly && DateTime.UtcNow - File.GetLastWriteTimeUtc(newest) > TimeSpan.FromMinutes(30))
            _status.Text = $"The newest screenshot is from {File.GetLastWriteTime(newest):d MMM HH:mm}. Press Print Screen at the terminal and then Newest screenshot, or type the prices in.";
        else await LoadScreenshotAsync(newest, null);
    }

    private async Task ImportAsync()
    {
        var folder = _context.Host.ScreenshotFolder();
        using var picker = new OpenFileDialog
        {
            Title = "Select a commodity terminal screenshot",
            Filter = "Screenshot images|*.jpg;*.jpeg;*.png|All files|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (picker.ShowDialog(this) == DialogResult.OK) await LoadScreenshotAsync(picker.FileName, null);
    }

    private async Task ShowNextWaitingAsync()
    {
        if (_pending.Count == 0 || !MayReplaceRows()) return;
        var (path, scan) = _pending.Dequeue();
        ShowWaiting();
        await LoadScreenshotAsync(path, scan, asked: true);
    }

    private void ShowWaiting()
    {
        _waiting.Visible = _pending.Count > 0;
        _waiting.Text = $"{_pending.Count:N0} terminal screenshot(s) waiting";
    }

    private bool MayReplaceRows() => !Rows().Any(x => DraftOf(x).Saved is null && !IsBlank(x)) ||
        MessageBox.Show(this, "The rows that are not saved yet will be replaced. Continue?", "Market reporter", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private async Task LoadScreenshotAsync(string path, TerminalScan? scan, bool asked = false)
    {
        if (_reading || !asked && !MayReplaceRows()) return;
        if (scan is null)
        {
            if (_context.Host.Ocr is not { } ocr)
            {
                _status.Text = "Windows OCR is unavailable, so screenshots cannot be read. Type the prices in instead.";
                return;
            }
            _reading = true;
            _status.Text = $"Reading {Path.GetFileName(path)}…";
            try { scan = await Task.Run(() => TerminalReader.ReadAsync(ocr, path, CancellationToken.None)); }
            catch (Exception ex)
            {
                _status.Text = $"{Path.GetFileName(path)} could not be read: {ex.Message}";
                return;
            }
            finally { _reading = false; }
            _context.Store.MarkScreenshot(path, scan.IsTerminal ? "terminal" : "other");
        }
        ShowScan(path, scan);
    }

    private void ShowScan(string path, TerminalScan scan)
    {
        _loading = true;
        _grid.Rows.Clear();
        _screenshot = path;
        _scan = scan;
        ShowImage(path);
        _seen.Value = Clamp(File.GetLastWriteTime(path));
        var remarks = new List<string>(scan.Warnings);
        var named = scan.LocationHint.Length > 0 ? NameMatcher.Terminal(scan.LocationHint, _context.Terminals) : null;
        var guessed = named;
        var traded = new Dictionary<(int Commodity, bool Buy), double?>();

        foreach (var price in scan.Prices)
        {
            var match = NameMatcher.Commodity(price.Name, _context.Commodities);
            if (match is not null && price.IsBuy is { } side) traded[(match.Id, side)] = price.Price;
            var row = _grid.Rows[_grid.Rows.Add()];
            var draft = new RowDraft { Source = PriceSource.Screenshot, Notes = [.. price.Notes], Doubts = [.. price.Doubts], Region = price.Region, IsBuy = price.IsBuy, PriceOnly = !price.HasShopQuantity };
            if (match is null) draft.Doubts.Insert(0, $"\"{price.Name}\" is no commodity UEX knows: pick it from the list.");
            row.Tag = draft;
            row.Cells[Commodity].Value = match?.Name;
            // A price whose side is unknown goes in neither column. Started under Buy, it is one tick away from
            // a report on the wrong side, so the player types it where the screenshot shows it belongs.
            if (price.IsBuy is { } isBuy) row.Cells[isBuy ? Buy : Sell].Value = price.Price;
            else
            {
                var read = price.Price is { } seen ? $" The price read is {seen:#,0.##}." : "";
                draft.Doubts.Insert(0, scan.Transaction is null
                    ? $"Could not tell which shop tab is lit.{read} Type the price under Buy if the BUY tab is lit, under Sell if LOCAL MARKET VALUE is."
                    : $"Could not tell a purchase from a sale.{read} Type the price under Buy if you bought, under Sell if you sold.");
            }
            row.Cells[Stock].Value = price.ShopScu;
            row.Cells[Status].Value = TradeUi.Status(price.Status);
            row.Cells[Crates].Value = price.ContainerSizes;
            // Such a row has no shop figures and gets none: what the player carries, typed in as the terminal's
            // stock, went to UEX as a report. Without them the row stays on this PC.
            if (draft.PriceOnly)
                foreach (var column in new[] { Stock, Status, Crates })
                {
                    row.Cells[column].ReadOnly = true;
                    row.Cells[column].Style.BackColor = Color.Gainsboro;
                    row.Cells[column].ToolTipText = "The screenshot shows no shop figures for this row, so it stays on this PC. UEX gets its prices from the shop list under BUY and LOCAL MARKET VALUE.";
                }
        }
        // When the screen does not name the station, UEX's own price list says which terminal trades this set
        // of commodities. A price saved under the wrong terminal would spoil the routes and a UEX report alike.
        if (named is null && traded.Count >= 2)
        {
            // How many of the commodities UEX lists at a terminal, and for how many of those its price is within 2%.
            (int Listed, int Priced) Fit(UexTerminal terminal)
            {
                var known = traded.Select(x => (Seen: x.Value, Uex: _context.UexQuote(terminal.Id, x.Key.Commodity, x.Key.Buy))).Where(x => x.Uex is not null).ToList();
                return (known.Count, known.Count(x => x.Seen is > 0 && Math.Abs(x.Seen.Value - x.Uex!.Price) <= x.Uex.Price * 0.02));
            }
            var ranked = _context.Terminals.Select(x => (Terminal: x, Fit: Fit(x))).OrderByDescending(x => x.Fit.Listed).ThenByDescending(x => x.Fit.Priced).Take(2).ToList();
            // Only a terminal that fits better than every other one is worth naming.
            if (ranked.Count > 0 && ranked[0].Fit.Listed >= 2 && (ranked.Count == 1 || ranked[0].Fit != ranked[1].Fit))
            {
                var here = Terminal is null ? 0 : Fit(Terminal).Listed;
                if (Terminal is null) guessed = ranked[0].Terminal;
                else if (ranked[0].Terminal.Id != Terminal.Id && here * 2 < ranked[0].Fit.Listed)
                    remarks.Insert(0, $"Is the terminal right? UEX lists {ranked[0].Fit.Listed:N0} of these {traded.Count:N0} commodities at {ranked[0].Terminal.Label} and {here:N0} at {Terminal.Label}.");
            }
        }
        if (guessed is not null && guessed.Id != Terminal?.Id) SelectTerminal(guessed);
        else guessed = null;
        _loading = false;
        RefreshRows();
        ShowRowImage();

        // Guide: "The screenshot must show ... the correct location or shop."
        if (scan.Prices.Count > 0 && scan.Transaction is null && !NameMatcher.IsAPlace(scan.LocationHint, _context.Terminals))
            remarks.Add("The picture does not name the station. UEX asks for proof that shows the location: on the terminal, pick the station under YOUR INVENTORIES before you press Print Screen.");
        if (scan.Transaction is { } done)
            remarks.Insert(0, $"This screenshot confirms a {(done.IsPurchase == false ? "sale" : "purchase")}: {done.Quantity:N0} × {done.Name} at {done.UnitPrice:#,0.##}" +
                              (done.Total is > 0 ? $", {done.Total:N0} aUEC in total" : "") + (done.Fee is > 0 ? $" including {done.Fee:N0} for auto-loading" : "") +
                              ". That is your transaction, not the terminal's stock. Log trade puts it in the journal.");
        _remarks = string.Join(Environment.NewLine, remarks);
        ShowInfo();
        _status.Text = !scan.IsTerminal
            ? "No commodity terminal was recognized on this screenshot. You can still add rows by hand."
            : $"Read {scan.Prices.Count:N0} row(s) from {Path.GetFileName(path)}" + (guessed is null ? "" : $" and set the terminal to {guessed.Label}" + (named is null ? " (guessed from the prices: check it)" : "")) +
              ". Compare them with the screenshot, tick OK (or Confirm all), then Save.";
    }

    // Batch remarks first, then whatever there is to say about the current row, in full.
    private void ShowInfo() => _info.Text = string.Join(Environment.NewLine,
        new[] { _remarks, _grid.CurrentRow?.Cells[Check].Value as string }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private DateTime Clamp(DateTime time) => time < _seen.MinDate ? _seen.MinDate : time > _seen.MaxDate ? _seen.MaxDate : time;

    private void ShowImage(string? path)
    {
        var shown = _image.Image;
        _image.Image = null;
        if (!ReferenceEquals(shown, _shot)) shown?.Dispose();
        _shot?.Dispose();
        _shot = null;
        if (path is null) return;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var source = Image.FromStream(stream);
            _image.Image = _shot = new Bitmap(source);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException) { }
    }

    // Zooms in on the part of the screenshot the current row came from; the whole picture for a typed-in row.
    private void ShowRowImage()
    {
        if (_shot is null) return;
        var area = _grid.CurrentRow?.Tag is RowDraft { Region: { } region } ? Rectangle.Intersect(region, new Rectangle(Point.Empty, _shot.Size)) : Rectangle.Empty;
        var shown = _image.Image;
        _image.Image = area is { Width: > 8, Height: > 8 } ? _shot.Clone(area, _shot.PixelFormat) : _shot;
        if (!ReferenceEquals(shown, _shot)) shown?.Dispose();
    }

    private void OpenScreenshot()
    {
        if (File.Exists(_screenshot)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_screenshot) { UseShellExecute = true });
    }

    private IEnumerable<DataGridViewRow> Rows() => _grid.Rows.Cast<DataGridViewRow>();

    private static RowDraft DraftOf(DataGridViewRow row) => (RowDraft)(row.Tag ??= new RowDraft());

    private static bool IsBlank(DataGridViewRow row) => new[] { Commodity, Buy, Sell, Stock, Demand }.All(x => row.Cells[x].Value is null or DBNull or "");

    private bool BatchIsSaved() => _grid.Rows.Count > 0 && Rows().All(x => DraftOf(x).Saved is not null);

    // Empties the grid and lets go of the screenshot, so the next rows are not filed under the last picture and time.
    private void StartNewBatch()
    {
        _loading = true;
        _grid.Rows.Clear();
        _loading = false;
        (_screenshot, _scan, _remarks) = ("", null, "");
        ShowImage(null);
        ShowInfo();
        _seen.Value = Clamp(DateTime.Now);
    }

    private void AddRow()
    {
        if (BatchIsSaved() || _grid.Rows.Count == 0 && _screenshot.Length == 0) StartNewBatch();
        var row = _grid.Rows[_grid.Rows.Add()];
        row.Tag = new RowDraft();
        // A row typed in by the player is the player's own reading, so it starts confirmed.
        _loading = true;
        row.Cells[Ok].Value = true;
        _loading = false;
        _grid.CurrentCell = row.Cells[Commodity];
        _grid.Focus();
    }

    private void RemoveRow()
    {
        if (_grid.CurrentRow is not { } row) return;
        if (DraftOf(row).Saved is not null) _status.Text = "A saved observation stays in the history. Delete it on the Market history tab if it is wrong.";
        else _grid.Rows.Remove(row);
    }

    // The observation a row describes, whether or not it is complete.
    private MarketObservation Read(DataGridViewRow row)
    {
        var draft = DraftOf(row);
        var commodity = _context.Commodities.FirstOrDefault(x => x.Name == row.Cells[Commodity].Value as string);
        var sizes = row.Cells[Crates].Value as string ?? "";
        return new MarketObservation
        {
            StarSystem = Terminal?.StarSystemName ?? "", TerminalId = Terminal?.Id ?? 0, TerminalName = Terminal?.Label ?? "",
            CommodityId = commodity?.Id ?? 0, CommodityName = commodity?.Name ?? "",
            PriceBuy = Number(row, Buy), PriceSell = Number(row, Sell), StockScu = (int?)Number(row, Stock), DemandScu = (int?)Number(row, Demand),
            Status = Array.IndexOf(TradeUi.StatusNames, row.Cells[Status].Value as string ?? "") is > 0 and var status ? status : null,
            ContainerSizes = ObservationValidator.NormalizeSizes(sizes) ?? sizes,
            ObservedUtc = _seen.Value.ToUniversalTime(), Source = draft.Source, Verified = row.Cells[Ok].Value is true,
            // The picture stays with every row of the batch: it is the evidence UEX asks new data runners for.
            ScreenshotPath = _screenshot, Notes = string.Join(" ", draft.Doubts.Concat(draft.Notes))
        };
    }

    private static double? Number(DataGridViewRow row, int column) => row.Cells[column].Value switch
    {
        double value => value,
        int value => value,
        _ => null
    };

    private void RefreshRows()
    {
        foreach (var row in Rows()) RefreshRow(row);
    }

    private void RefreshRow(DataGridViewRow row)
    {
        var draft = DraftOf(row);
        if (draft.Saved is { } saved)
        {
            row.Cells[Check].Value = saved.Submission switch
            {
                SubmissionState.Submitted => "Saved and sent to UEX ✓",
                SubmissionState.Test => "Saved; UEX accepted it as a test ✓",
                SubmissionState.Failed => $"Saved; UEX refused: {saved.SubmissionDetail}",
                _ => saved.Verified ? "Saved ✓" : "Saved, but not checked: tick Checked when it matches the terminal"
            };
            // Everything but the tick is fixed once saved, and the tick too once the row has gone to UEX.
            foreach (DataGridViewCell cell in row.Cells) cell.ReadOnly = cell.ColumnIndex != Ok || saved.Submission == SubmissionState.Submitted;
            row.DefaultCellStyle.BackColor = Color.White;
            row.DefaultCellStyle.ForeColor = Color.DimGray;
            if (row == _grid.CurrentRow) ShowInfo();
            return;
        }
        var observation = Read(row);
        var uex = observation.CommodityId > 0 && Terminal is not null ? _context.UexQuote(observation.TerminalId, observation.CommodityId, observation.PriceSell is not > 0) : null;
        var errors = IsBlank(row) ? [] : Errors(row, observation);
        var warning = Warning(observation);
        // The tick is left alone while a row is being typed: an unfinished row is refused when saving anyway.
        var loading = _loading;
        _loading = true;
        row.Cells[Uex].Value = uex?.Price;
        row.Cells[UexAge].Value = TradeUi.Age(uex?.AsOfUtc, DateTime.UtcNow);
        row.Cells[Delta].Value = uex is not null && observation.Price is > 0 ? $"{(observation.Price.Value - uex.Price) / uex.Price * 100:+0.0;-0.0;0.0} %" : "";
        row.Cells[Check].Value = string.Join(" ", errors.Concat(warning is null ? [] : [warning]).Concat(draft.Doubts).Concat(draft.Notes));
        _loading = loading;
        row.DefaultCellStyle.BackColor = errors.Count > 0 ? Color.MistyRose : warning is not null || draft.Doubts.Count > 0 ? Color.LemonChiffon : Color.White;
        if (row == _grid.CurrentRow) ShowInfo();
    }

    // What keeps a row from being saved: the validator's reasons, and a price under the column the screenshot
    // contradicts. UEX's staff compare each report with its screenshot, and so does this.
    private static List<string> Errors(DataGridViewRow row, MarketObservation observation)
    {
        var errors = ObservationValidator.Errors(observation, DateTime.UtcNow);
        if (DraftOf(row).IsBuy is { } isBuy && (isBuy ? observation.PriceSell : observation.PriceBuy) is > 0)
            errors.Add((isBuy ? "On the screenshot this is a price you pay: it goes under Buy, not Sell." : "On the screenshot this is a price the terminal pays you: it goes under Sell, not Buy.") +
                       " If the screenshot shows otherwise, remove the row and add it by hand.");
        return errors;
    }

    // What UEX's own list makes doubtful about a row. Such a row is not confirmed in bulk.
    private string? Warning(MarketObservation observation)
    {
        var uex = _context.Price(observation.TerminalId, observation.CommodityId);
        return ObservationValidator.PriceWarning(observation.Price, uex?.Quote(observation.IsBuy)?.Price) ?? ObservationValidator.SideWarning(observation, uex);
    }

    // Stores the tick of saved rows. True when anything changed.
    private bool SetChecked(IEnumerable<(MarketObservation Saved, bool Checked)> rows)
    {
        var changed = false;
        foreach (var (saved, isChecked) in rows.Where(x => x.Saved.Verified != x.Checked && x.Saved.Submission != SubmissionState.Submitted))
        {
            _context.Store.SetVerified(saved.Id, isChecked);
            saved.Verified = isChecked;
            changed = true;
        }
        return changed;
    }

    // Ticks every row that has no error and nothing guessed about it, saved or not.
    private void ConfirmAll()
    {
        _grid.EndEdit();
        int ticked = 0, left = 0;
        var saved = new List<(MarketObservation, bool)>();
        _loading = true;
        foreach (var row in Rows().Where(x => !IsBlank(x) && x.Cells[Ok].Value is not true))
        {
            var observation = DraftOf(row).Saved ?? Read(row);
            // A row with a doubt was partly guessed, so each of those needs its own tick.
            if (Errors(row, observation).Count > 0 || DraftOf(row).Doubts.Count > 0 || Warning(observation) is not null)
            {
                left++;
                continue;
            }
            row.Cells[Ok].Value = true;
            ticked++;
            if (DraftOf(row).Saved is { } stored) saved.Add((stored, true));
        }
        _loading = false;
        if (SetChecked(saved)) _context.NotifyChanged();
        RefreshRows();
        _status.Text = $"{ticked:N0} row(s) marked as checked." + (left > 0 ? $" {left:N0} row(s) have an error or a doubt: look at those and tick them yourself." : "") +
                       (Rows().Any(x => DraftOf(x).Saved is null && !IsBlank(x)) ? " Now press Save." : "");
    }

    // False when the player backed out.
    private bool Save()
    {
        _grid.EndEdit();
        if (Terminal is null)
        {
            _status.Text = "Pick the terminal first.";
            return false;
        }
        // Saving rows nobody has ticked is almost always an oversight, so it is asked about, not done silently.
        var unsaved = Rows().Where(x => DraftOf(x).Saved is null && !IsBlank(x)).ToList();
        if (unsaved.Count > 0 && unsaved.All(x => x.Cells[Ok].Value is not true))
        {
            var answer = MessageBox.Show(this,
                $"None of the {unsaved.Count:N0} row(s) is ticked as checked.\n\nOnly checked rows are used in Trade ideas and can be sent to UEX.\n\n" +
                "Have you compared them with the terminal?\n\nYes: mark the rows without remarks as checked, then save.\nNo: save them unchecked. You can tick them later.",
                "Market reporter", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes) ConfirmAll();
        }
        var ready = new List<(DataGridViewRow Row, MarketObservation Observation)>();
        var skipped = 0;
        foreach (var row in Rows().Where(x => DraftOf(x).Saved is null && !IsBlank(x)))
        {
            var observation = Read(row);
            if (Errors(row, observation).Count > 0) skipped++;
            else ready.Add((row, observation));
        }
        if (ready.Count == 0)
        {
            _status.Text = skipped > 0 ? "Nothing was saved: the rows in red need fixing first." : "There is nothing to save.";
            return true;
        }

        // Reading the same screenshot again must not store everything twice. A row that is already there is taken
        // over as it is, and a tick given now is added to it.
        var known = _screenshot.Length == 0 ? [] : _context.Store.Observations(Terminal.Id).Where(x => x.Source != PriceSource.Uex && x.ScreenshotPath == _screenshot).ToList();
        var again = new List<(MarketObservation, bool)>();
        foreach (var (row, observation) in ready.ToList())
        {
            if (known.FirstOrDefault(x => x.CommodityId == observation.CommodityId && x.ObservedUtc == observation.ObservedUtc && x.PriceBuy == observation.PriceBuy &&
                                          x.PriceSell == observation.PriceSell && x.StockScu == observation.StockScu && x.Status == observation.Status &&
                                          x.ContainerSizes == observation.ContainerSizes) is not { } stored) continue;
            ready.Remove((row, observation));
            DraftOf(row).Saved = stored;
            if (observation.Verified) again.Add((stored, true));
        }
        var updated = SetChecked(again);

        // What UEX said at the same moment is kept next to it, so the history can show the two side by side.
        var reports = ready.Select(x => (x.Observation, Quote: _context.UexQuote(x.Observation.TerminalId, x.Observation.CommodityId, x.Observation.IsBuy)))
            .Where(x => x.Quote?.AsOfUtc is not null).Select(x => new MarketObservation
            {
                StarSystem = x.Observation.StarSystem, TerminalId = x.Observation.TerminalId, TerminalName = x.Observation.TerminalName,
                CommodityId = x.Observation.CommodityId, CommodityName = x.Observation.CommodityName,
                PriceBuy = x.Observation.IsBuy ? x.Quote!.Price : null, PriceSell = x.Observation.IsBuy ? null : x.Quote!.Price,
                StockScu = (int?)x.Quote!.Scu, ContainerSizes = x.Quote.ContainerSizes ?? "", ObservedUtc = x.Quote.AsOfUtc!.Value, Source = PriceSource.Uex
            });
        try { _context.Store.Add(ready.Select(x => x.Observation).Concat(reports)); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _status.Text = $"The observations could not be saved: {ex.Message}";
            return true;
        }
        foreach (var (row, observation) in ready) DraftOf(row).Saved = observation;
        _context.NotifyChanged();
        var all = Rows().Select(x => DraftOf(x).Saved).OfType<MarketObservation>().ToList();
        var unticked = all.Count(x => !x.Verified);
        _status.Text = (ready.Count > 0 ? $"Saved {ready.Count:N0} observation(s). " : updated ? "These were saved before; the ticks are stored now. " : "These were saved before. ") +
                       (unticked == 0 ? "All are checked, so Trade ideas uses them from now on."
                           : $"{unticked:N0} of {all.Count:N0} are not checked yet and are not used: tick Checked on the ones that match the terminal.") +
                       (skipped > 0 ? $" {skipped:N0} row(s) in red were left out." : "");
        return true;
    }

    private async Task SubmitAsync()
    {
        _grid.EndEdit();
        if (Rows().Any(x => DraftOf(x).Saved is null && !IsBlank(x)) && !Save()) return;
        var rows = Rows().Select(x => DraftOf(x).Saved).OfType<MarketObservation>()
            .Where(x => x.Verified && x.Submission != SubmissionState.Submitted).ToList();
        if (rows.Count == 0)
        {
            _status.Text = "Nothing was sent: no row is checked. Tick Checked on the rows that match the terminal (or press Confirm all), then press Submit to UEX again.";
            return;
        }
        _submit.Enabled = false;
        try { _status.Text = await UexSubmitFlow.RunAsync(this, _context, rows); }
        finally { _submit.Enabled = true; }
        RefreshRows();
    }

    private void LogTrade()
    {
        if (Terminal is not { } terminal)
        {
            _status.Text = "Pick the terminal first.";
            return;
        }
        var seen = _seen.Value.ToUniversalTime();
        var done = _scan?.Transaction;
        var row = _grid.CurrentRow is { } current && !IsBlank(current) ? Read(current) : null;
        var commodity = done is not null ? NameMatcher.Commodity(done.Name, _context.Commodities)
            : _context.Commodities.FirstOrDefault(x => x.Id == row?.CommodityId);
        var selling = done is not null ? done.IsPurchase == false : row is { IsBuy: false, PriceSell: > 0 };
        var price = done?.UnitPrice ?? row?.Price;

        // A sale closes the open run of the same commodity; anything else starts a new run here.
        var trade = selling ? _context.Store.Trades().FirstOrDefault(x => !x.IsCompleted && x.CommodityId == commodity?.Id) : null;
        trade ??= new TradeRecord { Ship = _context.Host.Ship().Name, CommodityId = commodity?.Id ?? 0, CommodityName = commodity?.Name ?? "", BoughtUtc = seen, Scu = done?.Quantity ?? 0 };
        if (selling)
        {
            (trade.DestinationId, trade.DestinationName, trade.PriceSell, trade.SoldUtc) = (terminal.Id, terminal.Label, price, seen);
            trade.UnloadingSeconds ??= done?.Seconds;
        }
        else
        {
            (trade.OriginId, trade.OriginName, trade.PriceBuy) = (terminal.Id, terminal.Label, price ?? 0);
            trade.LoadingSeconds ??= done?.Seconds;
            trade.OperatingCosts ??= done?.Fee;
        }
        using var editor = new TradeEditorForm(_context, trade);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _context.Store.Save(editor.Trade);
        _context.NotifyChanged();
        _status.Text = editor.Trade.IsCompleted ? "The completed run is in the trading journal." : "The run is in the trading journal as open. Complete it when the cargo is sold.";
    }

    // Looks for screenshots taken since the window opened and reads them in the background. Nothing pops up:
    // a recognized terminal waits behind the "waiting" button, or fills the grid when the grid is empty.
    private async Task WatchAsync()
    {
        if (_reading || _context.Host.Ocr is not { } ocr) return;
        string? next;
        try
        {
            next = Screenshots().Where(x => File.GetLastWriteTimeUtc(x) is var written && written >= _openedUtc && written <= DateTime.UtcNow.AddMilliseconds(-800))
                .Where(x => !_context.Store.HasSeenScreenshot(x) && _pending.All(p => p.Path != x)).OrderBy(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (next is null) return;
        _reading = true;
        try
        {
            var scan = await Task.Run(() => TerminalReader.ReadAsync(ocr, next, CancellationToken.None));
            _context.Store.MarkScreenshot(next, scan.IsTerminal ? "terminal" : "other");
            if (!scan.IsTerminal) return;
            if (_grid.Rows.Count == 0) ShowScan(next, scan);
            else
            {
                _pending.Enqueue((next, scan));
                ShowWaiting();
            }
        }
        catch (Exception)
        {
            // An unreadable picture is skipped for good rather than retried every three seconds.
            _context.Store.MarkScreenshot(next, "unreadable");
        }
        finally { _reading = false; }
    }
}
