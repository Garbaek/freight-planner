using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FreightCopilot;

public sealed class MainForm : Form
{
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private readonly LocalOcrService? _ocr;
    private readonly string? _ocrError;
    private readonly ProcessedImageRegistry _imageRegistry;
    private readonly TravelTimeDatabase _travelTimes;
    private readonly ActiveSessionStore _sessionStore;
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ContractScan> _contracts = [];
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _planTimer = new() { Interval = 4500 };
    private readonly System.Windows.Forms.Timer _countdownTimer = new() { Interval = 30_000 };
    private AppSettings _settings = new();
    private ManifestResult? _currentManifest;
    private string? _planWarning;
    private string? _lastPlannedFingerprint;
    private string? _lastSavedPlanFingerprint;
    private string? _lastHistoryFile;
    private string _displayedSelectionKey = "";
    private string _timerOverrideSelectionKey = "";
    private bool _refreshingGrid;
    private bool _watching;
    private bool _processing;
    private bool _planning;
    private int _planRevision;
    private ExecutionProgress? _execution;
    private PlanPreference _planPreference = PlanPreference.Balanced;
    private string? _sortColumn;
    private bool _sortAscending = true;
    private readonly Dictionary<ContractScan, (string Key, Color Color)> _urgencyColors = [];
    private AuthSession? _authSession;
    private string _apiBaseUrl = "https://freightplanner.cipherdispatch.com";
    private string _tradeStation = "";
    private TradeIdeasForm? _tradeWindow;
    private CancellationTokenSource? _receiverCancellation;
    private AccountClient? _pairingClient;
    private PairingRegistration? _pairingRegistration;
    private readonly SemaphoreSlim _mobilePlanPublishGate = new(1, 1);
    private readonly SemaphoreSlim _uexTradeSyncGate = new(1, 1);
    private readonly SemaphoreSlim _accountRefreshGate = new(1, 1);
    private string _lastPublishedMobilePlan = "";
    private long _mobilePlanRevision;

    private PairingForm? _pairingForm;
    private bool _tabletLinked;
    private TabletPlan? _tabletPlan;
    // A trade run from Trade ideas, when that is what the tablet shows. The tablet holds one thing at a time,
    // so this and _tabletPlan are never both set.
    private TradeRun? _tabletRun;
    // The Windows monitor can stay open if a contract plan later replaces the trade run on the tablet.
    private TradeRun? _activeTradeRun;
    private TradeRunMonitorForm? _tradeRunMonitor;

    // The plan as it was last sent. The tablet keeps showing this one until Send to tablet is pressed again,
    // so its checklist ticks are matched against these stops and not against whatever the PC plan has become.
    private sealed record TabletPlan(string Key, string Fingerprint, string ShipName, int Jobs, string Title, string Summary,
        IReadOnlyList<string> Route, IReadOnlyList<MobilePlanStop> Stops)
    {
        public ExecutionProgress? Progress { get; set; }
        public long FirstRevision { get; set; }
    }

    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly Button _findFolder = new() { Text = "Find", AutoSize = true, Margin = new Padding(3, 1, 0, 0) };
    private readonly ComboBox _ship = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems,
        IntegralHeight = false,
        DropDownHeight = 420
    };
    private readonly NumericUpDown _capacity = new() { Dock = DockStyle.Fill, Minimum = 1, Maximum = 5000 };
    private readonly LinkLabel _cargo = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 0, 0, 0) };
    private readonly Button _watch = new() { Text = "Start watching", AutoSize = true };
    private readonly Button _scanRecent = new() { Text = "Scan recent screenshots", AutoSize = true };
    private readonly Button _testOld = new() { Text = "Test old screenshots", AutoSize = true };
    private readonly Button _planNow = new() { Text = "Plan suggested run", AutoSize = true };
    private readonly Button _openRoute = new() { Text = "Open route view", AutoSize = true, Enabled = false };
    private readonly Button _execute = new() { Text = "Execute route", AutoSize = true, Enabled = false };
    private readonly Button _comparePlans = new() { Text = "Compare plans", AutoSize = true, Enabled = false };
    private readonly Button _editContract = new() { Text = "Edit / inspect contract", AutoSize = true, Enabled = false };
    private readonly Button _travelMap = new() { Text = "Travel map && times", AutoSize = true };
    private readonly Button _tradeIdeas = new() { Text = "Trade ideas", AutoSize = true };
    private readonly Button _tradeMonitor = new() { Text = "Trade monitor", AutoSize = true, Enabled = false };
    private readonly Button _openHistory = new() { Text = "Plan history", AutoSize = true };
    private readonly Button _forgetImages = new() { Text = "Forget old images", AutoSize = true };
    private readonly Button _clear = new() { Text = "Clear batch", AutoSize = true };
    private readonly Button _account = new() { Text = "Account", AutoSize = true };
    private readonly Button _pairPhone = new() { Text = "Pair phone", AutoSize = true, Enabled = false };
    private readonly Button _sendTablet = new() { Text = "Send to tablet", AutoSize = true, Enabled = false };
    private readonly Label _accountStatus = new() { Text = "Signed out", AutoSize = true, Padding = new Padding(4, 7, 8, 0) };
    private readonly Label _status = new() { Text = "Starting local OCR…", AutoSize = true };
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = false, AutoGenerateColumns = false, AllowUserToAddRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly TextBox _manifest = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 11) };

    private readonly IReadOnlyList<ShipProfile> _ships;

    public MainForm()
    {
        _ships = ShipCatalog.Load(AppContext.BaseDirectory);
        _imageRegistry = new ProcessedImageRegistry(AppContext.BaseDirectory);
        _travelTimes = new TravelTimeDatabase(AppContext.BaseDirectory);
        _sessionStore = new ActiveSessionStore(AppContext.BaseDirectory);
        try { _ocr = new LocalOcrService(); }
        catch (Exception ex) { _ocrError = ex.Message; }
        Text = "Freight Planner — Local OCR";
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        LoadSettings();
        _ = SendAnonymousLaunchAsync();
        RestoreSession();
        _status.Text = _activeTradeRun is { } restoredRun ? $"Restored trade run — {restoredRun.Describe()}" : _ocr is null ? "Local OCR unavailable" : _contracts.Count > 0
            ? $"Local OCR ready — restored {_contracts.Count:N0} active contract(s)"
            : $"Local OCR ready — {_ocr.EngineDescription}";
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _planTimer.Tick += async (_, _) => { _planTimer.Stop(); await PlanBatchAsync(false); };
        _countdownTimer.Tick += (_, _) => RefreshManifest(countdownTick: true);
        _countdownTimer.Start();
        Shown += async (_, _) =>
        {
            var accountRestore = RestoreAccountAsync();
            if (!Directory.Exists(_folder.Text)) await FindScreenshotFolderAsync(announce: false);
            if (_contracts.Count > 0 && _currentManifest is null) await PlanBatchAsync(false);
            if (_activeTradeRun is not null) OpenTradeRunMonitor();
            await accountRestore;
        };
        FormClosing += (_, _) => { SaveSettings(); SaveSession(); _receiverCancellation?.Cancel(); _pollTimer.Stop(); _planTimer.Stop(); _countdownTimer.Stop(); };
    }

    private void BuildUi()
    {
        var settings = new TableLayoutPanel { Dock = DockStyle.Top, Height = 158, ColumnCount = 4, RowCount = 3, Padding = new Padding(10) };
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        settings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.Controls.Add(_folder, 0, 0);
        folderRow.Controls.Add(_findFolder, 1, 0);
        var capacityRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        capacityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        capacityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        capacityRow.Controls.Add(_capacity, 0, 0);
        capacityRow.Controls.Add(_cargo, 1, 0);
        AddRow(settings, 0, "Screenshots", folderRow, "Ship", _ship);
        AddRow(settings, 1, "Status", _status, "Capacity", capacityRow);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.AddRange([_watch, _scanRecent, _testOld, _planNow, _comparePlans, _openRoute, _execute, _editContract, _travelMap, _tradeIdeas, _tradeMonitor, _openHistory, _forgetImages, _clear, _account, _pairPhone, _sendTablet, _accountStatus]);
        settings.Controls.Add(buttons, 1, 2);
        settings.SetColumnSpan(buttons, 3);

        _ship.Items.AddRange(_ships.Select(x => x.Name).ToArray());
        _ship.SelectedIndexChanged += (_, _) =>
        {
            var selected = _ships.FirstOrDefault(x => x.Name == _ship.Text);
            if (selected is not null && selected.Name != "Custom") _capacity.Value = selected.CapacityScu;
            InvalidatePlan();
        };
        _ship.TextChanged += (_, _) => ShowCargo();
        _capacity.ValueChanged += (_, _) => { ShowCargo(); InvalidatePlan(); };
        _cargo.LinkClicked += (_, _) => MessageBox.Show(this, ShipCatalog.DescribeCargo(CurrentShip(), ShipCatalog.VerifiedForPatch(AppContext.BaseDirectory)),
            "Cargo grid", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _findFolder.Click += async (_, _) => await FindScreenshotFolderAsync(announce: true);
        _watch.Click += async (_, _) => await ToggleWatchingAsync();
        _scanRecent.Click += async (_, _) => await QueueRecentAsync();
        _testOld.Click += async (_, _) => await TestOldScreenshotsAsync();
        _planNow.Click += async (_, _) => { _lastPlannedFingerprint = null; await PlanBatchAsync(true); };
        _comparePlans.Click += async (_, _) => await ComparePlansAsync();
        _openRoute.Click += (_, _) => OpenRouteView();
        _execute.Click += (_, _) => OpenExecutionMode();
        _editContract.Click += (_, _) => EditSelectedContract();
        _travelMap.Click += (_, _) => OpenTravelMap();
        _tradeIdeas.Click += (_, _) => OpenTradeIdeas();
        _tradeMonitor.Click += (_, _) => OpenTradeRunMonitor();
        _openHistory.Click += (_, _) => OpenHistoryFolder();
        _forgetImages.Click += (_, _) => ForgetProcessedImages();
        _clear.Click += (_, _) =>
        {
            _contracts.Clear(); _queued.Clear(); _currentManifest = null; _planWarning = null;
            _execution = null;
            _lastPlannedFingerprint = null; _lastSavedPlanFingerprint = null; _displayedSelectionKey = "";
            _timerOverrideSelectionKey = "";
            _sendTablet.Enabled = false;
            _planRevision++;
            RefreshGrid();
            // Clearing a hauling-contract batch must not discard a live commodity run.
            SaveSession();
        };
        _account.Click += async (_, _) => await ManageAccountAsync();
        _pairPhone.Click += async (_, _) => await PairPhoneAsync();
        _sendTablet.Click += async (_, _) => await SendPlanToTabletAsync();

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Plan", DataPropertyName = "SelectedForPlan", Width = 48 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Quality", DataPropertyName = "Quality", Width = 80, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TimeLeft", HeaderText = "Time left", DataPropertyName = "TimeLeft", Width = 78, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Reward", HeaderText = "Reward", DataPropertyName = "Reward", Width = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0" } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SCU", DataPropertyName = "TotalScu", Width = 60, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Title", DataPropertyName = "Title", Width = 220, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Pickup", HeaderText = "Pickup", DataPropertyName = "Pickups", Width = 200, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Destination", HeaderText = "Destination", DataPropertyName = "Destinations", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.ColumnHeaderMouseClick += (_, e) => SortContractsBy(_grid.Columns[e.ColumnIndex].Name);
        _grid.SelectionChanged += (_, _) => _editContract.Enabled = _grid.CurrentRow?.DataBoundItem is ContractScan;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_refreshingGrid || e.RowIndex < 0 || e.ColumnIndex != _grid.Columns["Include"].Index) return;
            if (_grid.Rows[e.RowIndex].DataBoundItem is not ContractScan scan) return;
            if (!scan.SelectedForPlan)
            {
                scan.ManualOverride = false;
            }
            else if (scan.AvailabilityMinutes is > 0 && scan.RemainingMinutes(DateTime.UtcNow) <= 0)
            {
                scan.SelectedForPlan = false;
                _grid.InvalidateRow(e.RowIndex);
                MessageBox.Show("Its availability timer has expired.", "Cannot select contract", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (!scan.IsEligible)
            {
                if (!scan.CanUseManualOverride)
                {
                    scan.SelectedForPlan = false;
                    _grid.InvalidateRow(e.RowIndex);
                    MessageBox.Show($"{scan.Notes}{Environment.NewLine}{Environment.NewLine}Cargo quantity, pickup, and destination are required to build a route.",
                        "Cannot build a route for this contract", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    var choice = MessageBox.Show($"{scan.Notes}{Environment.NewLine}{Environment.NewLine}" +
                        "The cargo route is readable. Include this contract anyway? Unread rewards will not be added to the guaranteed total, and unread timers cannot be safety-checked.",
                        "Use contract anyway?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (choice == DialogResult.Yes) scan.ManualOverride = true;
                    else scan.SelectedForPlan = false;
                    _grid.InvalidateRow(e.RowIndex);
                }
            }
            ManualSelectionChanged();
        };
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].DataBoundItem is not ContractScan scan) return;
            var rowColor = scan.ManualOverride && scan.SelectedForPlan ? Color.LightCyan : !scan.IsEligible ? Color.MistyRose :
                scan.ContractDeadlineNotApplicable ? Color.FromArgb(220, 238, 255) : UrgencyColor(scan);
            _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = rowColor;
            _grid.Rows[e.RowIndex].DefaultCellStyle.SelectionBackColor = ControlPaint.Dark(rowColor, 0.12f);
            _grid.Rows[e.RowIndex].Cells["TimeLeft"].ToolTipText = scan.ContractDeadlineNotApplicable
                ? "Blue: already accepted; no offer countdown."
                : rowColor == Color.Honeydew ? "Green: comfortable estimated deadline margin."
                : rowColor == Color.LemonChiffon ? "Yellow: tight estimated deadline margin."
                : "Red: unread data, expired, or unlikely to finish with the safety reserve.";
            if (e.ColumnIndex == _grid.Columns.Cast<DataGridViewColumn>().First(x => x.DataPropertyName == "Reward").Index && scan.Reward <= 0)
            {
                e.Value = "Unknown";
                e.FormattingApplied = true;
            }
            // All checkboxes remain clickable. If a row is unsafe, the checked-change
            // handler clears it again and explains why instead of silently greying it out.
            if (e.ColumnIndex == _grid.Columns["Include"].Index)
                _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].ReadOnly = false;
        };
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].DataBoundItem is ContractScan scan) EditContract(scan);
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 370 };
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(_manifest);
        Controls.Add(split);
        Controls.Add(settings);
    }

    private static void AddRow(TableLayoutPanel panel, int row, string label1, Control control1, string label2, Control control2)
    {
        panel.Controls.Add(new Label { Text = label1, Anchor = AnchorStyles.Left, AutoSize = true }, 0, row);
        panel.Controls.Add(control1, 1, row);
        panel.Controls.Add(new Label { Text = label2, Anchor = AnchorStyles.Left, AutoSize = true }, 2, row);
        panel.Controls.Add(control2, 3, row);
    }

    private bool EnsureLocalOcr()
    {
        if (_ocr is not null) return true;
        _status.Text = "Local OCR unavailable";
        MessageBox.Show(_ocrError ?? "Windows OCR could not be initialized.", "Local OCR unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return false;
    }

    private async Task FindScreenshotFolderAsync(bool announce)
    {
        _findFolder.Enabled = false;
        var previousStatus = _status.Text;
        _status.Text = "Searching local drives for StarCitizen\\LIVE\\screenshots…";
        try
        {
            var found = await Task.Run(ScreenshotFolderLocator.Find);
            if (found is null)
            {
                _status.Text = "Star Citizen screenshot folder not found — enter it manually";
                if (announce)
                    MessageBox.Show(this, "No StarCitizen\\LIVE folder was found on this PC's drives. Paste the screenshot folder path manually.",
                        "Freight Planner", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _folder.Text = found;
            SaveSettings();
            _status.Text = Directory.Exists(found)
                ? $"Screenshot folder found — {found}"
                : "Star Citizen found — the screenshots folder appears after your first Print Screen in game";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = previousStatus;
        }
        finally { _findFolder.Enabled = true; }
    }

    private async Task ToggleWatchingAsync()
    {
        if (_watching)
        {
            _watching = false; _pollTimer.Stop(); _planTimer.Stop(); _watch.Text = "Start watching"; _status.Text = "Stopped";
            return;
        }
        if (!Directory.Exists(_folder.Text))
        {
            MessageBox.Show("The screenshot folder does not exist.", "Freight Planner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!EnsureLocalOcr()) return;
        SaveSettings();
        _watching = true;
        _watch.Text = "Stop watching";
        _status.Text = "Watching — Local OCR on this PC";
        _pollTimer.Start();
        await PollAsync();
    }

    private async Task QueueRecentAsync()
    {
        if (!Directory.Exists(_folder.Text))
        {
            MessageBox.Show("The screenshot folder does not exist.", "Freight Planner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!EnsureLocalOcr()) return;
        SaveSettings();
        var candidates = Directory.EnumerateFiles(_folder.Text, "*.jpg").Concat(Directory.EnumerateFiles(_folder.Text, "*.png"))
            .OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        // A capture session is a continuous burst. Do not top up a short current
        // session with contracts from last night merely to reach the 20-file limit.
        var files = NewestCaptureSession(candidates, TimeSpan.FromMinutes(10), 20)
            .Where(file => !_imageRegistry.IsIgnored(file)).ToList();
        if (files.Count == 0)
        {
            _status.Text = $"No new screenshots — {_imageRegistry.Count:N0} previously processed image(s) ignored";
            return;
        }
        foreach (var file in files) _queued.TryAdd(file, 0);
        await ProcessQueueAsync(planImmediately: true);
    }

    private async Task TestOldScreenshotsAsync()
    {
        if (!EnsureLocalOcr() || _ocr is null) return;
        using var picker = new OpenFileDialog
        {
            Title = "Select old contract screenshots to test",
            Filter = "Screenshot images|*.jpg;*.jpeg;*.png|All files|*.*",
            Multiselect = true,
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (picker.ShowDialog(this) != DialogResult.OK || picker.FileNames.Length == 0) return;

        var files = picker.FileNames.OrderBy(File.GetLastWriteTimeUtc).Take(60).ToList();
        var scans = new List<ContractScan>();
        var failures = new List<string>();
        _testOld.Enabled = false;
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                _status.Text = $"Historical OCR test {index + 1}/{files.Count} — {Path.GetFileName(file)}";
                try
                {
                    var text = await Task.Run(() => _ocr.ReadTextAsync(file, CancellationToken.None));
                    scans.Add(MissionParser.Parse(text, file));
                }
                catch (Exception ex) { failures.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
            }
        }
        finally { _testOld.Enabled = true; }

        if (scans.Count == 0)
        {
            _status.Text = "Historical OCR test failed";
            MessageBox.Show(string.Join(Environment.NewLine, failures.Take(5)), "No screenshots could be tested", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        MissionParser.ReconcileBatch(scans);
        var uniqueScans = ContractIdentity.CollapseDuplicates(scans);
        _status.Text = $"Historical test ready — {scans.Count:N0} screenshot(s), {uniqueScans.Count:N0} unique mission(s), {failures.Count:N0} file failure(s)";
        if (failures.Count > 0)
            MessageBox.Show(string.Join(Environment.NewLine, failures.Take(5)), "Some screenshots could not be read", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        new HistoricalTestForm(uniqueScans, _travelTimes, CurrentShip(), AppContext.BaseDirectory, scans.Count).Show(this);
    }

    public static List<string> NewestCaptureSession(IReadOnlyList<string> newestFirst, TimeSpan maximumGap, int maximumFiles)
    {
        if (newestFirst.Count == 0 || maximumFiles <= 0) return [];
        var selected = new List<string> { newestFirst[0] };
        var previous = File.GetLastWriteTimeUtc(newestFirst[0]);
        foreach (var file in newestFirst.Skip(1))
        {
            var captured = File.GetLastWriteTimeUtc(file);
            if (previous - captured > maximumGap || selected.Count >= maximumFiles) break;
            selected.Add(file);
            previous = captured;
        }
        selected.Reverse();
        return selected;
    }

    private async Task PollAsync()
    {
        if (!Directory.Exists(_folder.Text) || _processing) return;
        var cutoff = DateTime.UtcNow.AddSeconds(-8);
        foreach (var file in Directory.EnumerateFiles(_folder.Text)
                     .Where(x => x.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                     .Where(x => !_imageRegistry.IsIgnored(x))
                     .Where(x => File.GetLastWriteTimeUtc(x) >= cutoff && File.GetLastWriteTimeUtc(x) <= DateTime.UtcNow.AddMilliseconds(-500)))
            _queued.TryAdd(file, 0);
        await ProcessQueueAsync(planImmediately: false);
    }

    private async Task ProcessQueueAsync(bool planImmediately)
    {
        if (_processing || _queued.IsEmpty || _ocr is null) return;
        _processing = true;
        var errors = new List<string>();
        var processedAny = false;
        try
        {
            foreach (var file in _queued.Keys.OrderBy(File.GetLastWriteTimeUtc).ToList())
            {
                if (_contracts.Any(x => x.SourceFile.Equals(file, StringComparison.OrdinalIgnoreCase)))
                {
                    _imageRegistry.MarkProcessed(file, saveImmediately: false);
                    _queued.TryRemove(file, out _); continue;
                }
                try
                {
                    _status.Text = $"Local OCR — {Path.GetFileName(file)}…";
                    var text = await Task.Run(() => _ocr.ReadTextAsync(file, CancellationToken.None));
                    // A commodity terminal is not a contract; Trade ideas > Market reporter reads those.
                    if (TerminalScreenParser.LooksLikeTerminal(text))
                    {
                        _imageRegistry.MarkProcessed(file, saveImmediately: false);
                        _queued.TryRemove(file, out _);
                        continue;
                    }
                    _status.Text = $"Parsing — {Path.GetFileName(file)}…";
                    var scan = MissionParser.Parse(text, file);
                    var duplicate = _contracts.FirstOrDefault(x => ContractIdentity.SameMission(x, scan));
                    if (duplicate is null) _contracts.Add(scan);
                    else ContractIdentity.MergeBest(duplicate, scan);
                    _imageRegistry.MarkProcessed(file, saveImmediately: false);
                    _planRevision++;
                    processedAny = true;
                }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
                _queued.TryRemove(file, out _);
                RefreshGrid(refreshManifest: false);
            }
            if (processedAny) MissionParser.ReconcileBatch(_contracts);
        }
        finally
        {
            _imageRegistry.Flush();
            _processing = false;
        }

        if (errors.Count > 0)
        {
            _status.Text = $"{errors.Count} local OCR failure(s) — {_contracts.Count} records";
            MessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, errors.Take(3)) +
                            (errors.Count > 3 ? $"{Environment.NewLine}{Environment.NewLine}…and {errors.Count - 3} more." : ""),
                "Local OCR failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else _status.Text = _watching ? $"Watching — {_contracts.Count} records in batch" : $"Local OCR complete — {_contracts.Count} records";

        if (!processedAny) return;
        SaveSession();
        _planWarning = null; RefreshManifest();
        if (planImmediately) await PlanBatchAsync(false);
        else { _planTimer.Stop(); _planTimer.Start(); }
    }

    private async Task PlanBatchAsync(bool userRequested)
    {
        if (_planning) return;
        var profile = CurrentShip();
        var checkedContracts = CheckedContracts();
        if (checkedContracts.Count > 0)
        {
            var invalid = checkedContracts.Where(x => (!x.IsEligible && !(x.ManualOverride && x.CanUseManualOverride)) ||
                                                        (x.AvailabilityMinutes is > 0 && x.RemainingMinutes(DateTime.UtcNow) <= 0)).ToList();
            if (invalid.Count > 0)
            {
                _planWarning = "One or more checked contracts are no longer valid or have expired. Uncheck them before planning.";
                RefreshManifest();
                return;
            }
            if (checkedContracts.Sum(x => x.TotalScu) > profile.CapacityScu)
            {
                _planWarning = $"The checked jobs total {checkedContracts.Sum(x => x.TotalScu):N0} SCU, exceeding the {profile.CapacityScu:N0} SCU ship capacity.";
                _status.Text = "Checked jobs exceed ship capacity";
                RefreshManifest();
                return;
            }
            if (!CratePlanner.Fits(checkedContracts, profile.Grids))
            {
                _planWarning = $"The checked jobs come as {CratePlanner.DescribeContainers(checkedContracts)} crates, which do not fit the {profile.Name} cargo grid.";
                _status.Text = "Checked jobs do not fit the cargo grid";
                RefreshManifest();
                return;
            }
        }
        var revision = _planRevision;
        var selected = checkedContracts.ToList();
        var planningContracts = _contracts.ToList();
        var preference = _planPreference;
        _travelTimes.ActiveShipName = profile.Name;
        _planning = true;
        _planNow.Enabled = false;
        _status.Text = selected.Count > 0 ? "Building checked freight route…" : "Optimizing freight route in the background…";
        ManifestResult manifest;
        try
        {
            manifest = selected.Count > 0
                ? ManifestOptimizer.BuildSelected(selected, profile, travelTimes: _travelTimes)
                : await Task.Run(() => ManifestOptimizer.Build(planningContracts, profile,
                    travelTimes: _travelTimes, preference: preference));
        }
        catch (Exception ex)
        {
            _planWarning = ex.Message;
            _status.Text = "Local route planning failed";
            RefreshManifest();
            return;
        }
        finally
        {
            _planning = false;
            _planNow.Enabled = true;
        }
        if (revision != _planRevision)
        {
            _status.Text = "Contracts changed while planning — refreshing route…";
            _planTimer.Stop();
            _planTimer.Start();
            return;
        }
        _currentManifest = manifest;
        if (manifest.Contracts.Count == 0)
        {
            _planWarning = "No complete, non-suspicious contract bundle is available for route planning.";
            _status.Text = "Local validation found no safe manifest";
            RefreshManifest();
            return;
        }
        string? timerOverrideWarning = null;
        if (manifest.HasKnownAvailability && manifest.HeadroomMinutes < 0)
        {
            var overrideKey = TimerOverrideKey(selected, profile);
            if (!overrideKey.Equals(_timerOverrideSelectionKey, StringComparison.Ordinal))
            {
                var explanation = $"The checked jobs need about {manifest.EstimatedTripMinutes + manifest.SafetyMarginMinutes:N0} minutes including the safety reserve, " +
                                  $"but the tightest contract has only {manifest.TightestRemainingMinutes:N0} minutes left.";
                if (!userRequested)
                {
                    _planWarning = explanation + " Press Plan checked jobs if you want to override the timer budget.";
                    _status.Text = "Checked jobs do not fit the timer budget — manual override available";
                    RefreshManifest();
                    return;
                }
                var choice = MessageBox.Show(explanation + Environment.NewLine + Environment.NewLine +
                                             "Build and use this route anyway? Contract deadlines may expire before you finish.",
                    "Ignore timer budget?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (choice != DialogResult.Yes)
                {
                    _planWarning = explanation + " Uncheck one or more jobs, or press Plan checked jobs again to override.";
                    _status.Text = "Timer override cancelled";
                    RefreshManifest();
                    return;
                }
                _timerOverrideSelectionKey = overrideKey;
            }
            manifest.TimerBudgetOverridden = true;
            timerOverrideWarning = $"Timer safety check overridden by the user: estimated requirement " +
                                   $"{manifest.EstimatedTripMinutes + manifest.SafetyMarginMinutes:N0}m versus " +
                                   $"{manifest.TightestRemainingMinutes:N0}m remaining on the tightest contract.";
        }
        var fingerprint = string.Join("|", _contracts.Select(x => $"{x.SourceFile}:{x.Reward}:{x.TotalScu}:{x.RemainingMinutes(DateTime.UtcNow)}")) +
                          $"|mode:{(manifest.IsManualSelection ? "checked" : "automatic")}" +
                          $"|selected:{string.Join(',', manifest.Contracts.Select(x => Path.GetFileName(x.SourceFile)).OrderBy(x => x))}" +
                          $"|travel:{_travelTimes.Revision}|{profile.Name}|{profile.CapacityScu}";
        if (!userRequested && fingerprint == _lastPlannedFingerprint) return;
        _planWarning = timerOverrideWarning;
        try
        {
            _lastPlannedFingerprint = fingerprint;
            _status.Text = manifest.TimerBudgetOverridden
                ? $"Route ready with timer override — {manifest.TotalScu:N0}/{profile.CapacityScu:N0} SCU"
                : manifest.HasUnverifiedValues
                ? $"Local route ready — {manifest.TotalScu:N0}/{profile.CapacityScu:N0} SCU with manual override(s)"
                : $"Local route ready — {manifest.TotalScu:N0}/{profile.CapacityScu:N0} SCU validated";
        }
        catch (Exception ex)
        {
            _planWarning = ex.Message;
            _lastPlannedFingerprint = fingerprint;
            _status.Text = "Local route planning failed";
        }
        finally
        {
            RefreshManifest();
            SavePlanHistory(fingerprint, manifest, force: userRequested);
            SaveSession();
        }
    }

    private void InvalidatePlan()
    {
        _planRevision++;
        _urgencyColors.Clear();
        _timerOverrideSelectionKey = "";
        _currentManifest = null;
        _planWarning = null; _lastPlannedFingerprint = null; _lastSavedPlanFingerprint = null; RefreshManifest();
        if (_contracts.Count > 0 && !_processing)
        {
            _planTimer.Stop();
            _planTimer.Start();
        }
    }

    // The ship as picked, with the capacity as typed: its grids and note come from the catalog.
    private ShipProfile CurrentShip() => (_ships.FirstOrDefault(x => x.Name == _ship.Text) ?? new ShipProfile("", 0)) with
    {
        Name = _ship.Text.Length == 0 ? "ARGO RAFT" : _ship.Text, CapacityScu = (int)_capacity.Value
    };

    // The ship's grids in a few words next to the capacity; a click shows them one by one.
    private void ShowCargo() => _cargo.Text = ShipCatalog.SummarizeCargo(CurrentShip());

    private void RefreshGrid(bool refreshManifest = true)
    {
        _urgencyColors.Clear();
        _refreshingGrid = true;
        _grid.DataSource = null;
        _grid.DataSource = SortedContracts().ToList();
        UpdateSortGlyph();
        _refreshingGrid = false;
        if (refreshManifest) RefreshManifest();
    }

    private IEnumerable<ContractScan> SortedContracts()
    {
        if (_sortColumn is null)
            return _contracts.OrderByDescending(x => x.IsEligible).ThenByDescending(x => x.Reward);

        var textComparer = StringComparer.CurrentCultureIgnoreCase;
        return _sortColumn switch
        {
            "Pickup" => _sortAscending ? _contracts.OrderBy(x => x.Pickups, textComparer) : _contracts.OrderByDescending(x => x.Pickups, textComparer),
            "Destination" => _sortAscending ? _contracts.OrderBy(x => x.Destinations, textComparer) : _contracts.OrderByDescending(x => x.Destinations, textComparer),
            "TimeLeft" => _sortAscending ? _contracts.OrderBy(SortableTimeLeft) : _contracts.OrderByDescending(SortableTimeLeft),
            "Reward" => _sortAscending ? _contracts.OrderBy(x => x.Reward) : _contracts.OrderByDescending(x => x.Reward),
            _ => _contracts.OrderByDescending(x => x.IsEligible).ThenByDescending(x => x.Reward)
        };
    }

    private static int SortableTimeLeft(ContractScan scan)
    {
        if (scan.ContractDeadlineNotApplicable) return int.MaxValue;
        if (scan.AvailabilityMinutes is not > 0) return int.MaxValue - 1;
        return scan.RemainingMinutes(DateTime.UtcNow);
    }

    private void SortContractsBy(string columnName)
    {
        if (columnName is not ("Pickup" or "Destination" or "TimeLeft" or "Reward")) return;
        if (_sortColumn == columnName) _sortAscending = !_sortAscending;
        else { _sortColumn = columnName; _sortAscending = true; }
        RefreshGrid();
    }

    private void UpdateSortGlyph()
    {
        foreach (DataGridViewColumn column in _grid.Columns)
            column.HeaderCell.SortGlyphDirection = column.Name == _sortColumn
                ? (_sortAscending ? SortOrder.Ascending : SortOrder.Descending)
                : SortOrder.None;
    }

    private void RefreshManifest(bool countdownTick = false)
    {
        if (_ship.SelectedIndex < 0) return;
        var profile = CurrentShip();
        _travelTimes.ActiveShipName = profile.Name;
        var selected = CheckedContracts();
        var manifest = selected.Count > 0
            ? ManifestOptimizer.BuildSelected(selected, profile, travelTimes: _travelTimes)
            : _currentManifest is { Contracts.Count: > 0 }
                ? ManifestOptimizer.RefreshExisting(_currentManifest, profile, travelTimes: _travelTimes)
                : new ManifestResult { PlanningUtc = DateTime.UtcNow, Preference = _planPreference };
        if (selected.Count > 0 && manifest.HasKnownAvailability && manifest.HeadroomMinutes < 0 &&
            TimerOverrideKey(selected, profile).Equals(_timerOverrideSelectionKey, StringComparison.Ordinal))
            manifest.TimerBudgetOverridden = true;
        _currentManifest = manifest;
        var selectionKey = $"{(manifest.IsManualSelection ? "checked" : "automatic")}|safe:{(!manifest.HasKnownAvailability || manifest.HeadroomMinutes >= 0)}|override:{manifest.ManualOverrideCount}|" +
                           string.Join("|", manifest.Contracts.Select(x => x.SourceFile).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        if (countdownTick && _displayedSelectionKey.Length > 0 && !selectionKey.Equals(_displayedSelectionKey, StringComparison.OrdinalIgnoreCase))
        {
            if (manifest.TimerBudgetOverridden)
                _planWarning = $"Timer safety check remains overridden: {Math.Abs(manifest.HeadroomMinutes):N0}m beyond the current timer budget.";
            else
            {
                _planWarning = "The countdown changed the safest contract set; the local route has been refreshed.";
                _lastPlannedFingerprint = null;
                if (_watching && manifest.Contracts.Count > 0) { _planTimer.Stop(); _planTimer.Start(); }
            }
        }
        _displayedSelectionKey = selectionKey;
        _manifest.Text = ManifestOptimizer.Format(manifest, profile, _contracts, _planWarning);
        _openRoute.Enabled = manifest.Contracts.Count > 0;
        _execute.Enabled = manifest.Contracts.Count > 0;
        _comparePlans.Enabled = _contracts.Any(x => x.IsEligible) && CheckedContracts().Count == 0;
        _editContract.Enabled = _grid.CurrentRow?.DataBoundItem is ContractScan;
        UpdateSendTabletButton();
        _planNow.Text = CheckedContracts().Count > 0 ? $"Plan {CheckedContracts().Count} checked job(s)" : "Plan suggested run";
        if (countdownTick) _grid.Invalidate();
    }

    private List<ContractScan> CheckedContracts() => _contracts.Where(x => x.SelectedForPlan).ToList();

    private static string TimerOverrideKey(IReadOnlyList<ContractScan> contracts, ShipProfile ship) =>
        $"{ship.Name}|{ship.CapacityScu}|" + string.Join("|", contracts.Select(ContractIdentity.Fingerprint)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    private void ManualSelectionChanged()
    {
        _planRevision++;
        _urgencyColors.Clear();
        _timerOverrideSelectionKey = "";
        _planWarning = null;
        _lastPlannedFingerprint = null;
        _lastSavedPlanFingerprint = null;
        SaveSession();
        RefreshManifest();
    }

    private void OpenRouteView()
    {
        if (_currentManifest is null || _currentManifest.Contracts.Count == 0)
        {
            MessageBox.Show("Select valid contracts or let Freight Planner create a suggested run first.", "No route available", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        new RoutePlanForm(_currentManifest, CurrentShip(), _manifest.Text, _travelTimes).Show(this);
    }

    private void OpenExecutionMode()
    {
        if (_currentManifest is null || _currentManifest.Contracts.Count == 0) return;
        var fingerprint = ContractIdentity.PlanFingerprint(_currentManifest);
        if (_execution is null || !_execution.PlanFingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            if (_execution is not null && _execution.CompletedTasks.Values.Any(x => x) &&
                MessageBox.Show("The route has changed. Start a new execution checklist and replace the saved active run?", "Route changed",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _execution = new ExecutionProgress
            {
                PlanFingerprint = fingerprint,
                ShipName = CurrentShip().Name,
                Route = _currentManifest.LocalRoute.ToList()
            };
            SaveSession();
        }
        var executionWindow = new ExecutionForm(_currentManifest, CurrentShip(), _travelTimes, _execution, progress =>
        {
            _execution = progress;
            SaveSession();
            if (_tabletPlan is not null) _ = PublishTabletPlanAsync(_tabletPlan);
        });
        executionWindow.FormClosed += (_, _) => InvalidatePlan();
        executionWindow.Show(this);
    }

    private async Task ComparePlansAsync()
    {
        if (CheckedContracts().Count > 0)
        {
            MessageBox.Show("Uncheck the manually selected jobs to compare automatic plan alternatives.", "Compare plans", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _travelTimes.ActiveShipName = CurrentShip().Name;
        var contracts = _contracts.ToList();
        var ship = CurrentShip();
        _comparePlans.Enabled = false;
        _status.Text = "Comparing route alternatives in the background…";
        List<PlanAlternative> alternatives;
        try
        {
            alternatives = await Task.Run(() => ManifestOptimizer.BuildAlternatives(contracts, ship, DateTime.UtcNow, _travelTimes));
        }
        finally
        {
            _comparePlans.Enabled = true;
        }
        if (alternatives.Count == 0)
        {
            MessageBox.Show("No safe automatic plan is currently available.", "Compare plans", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new PlanAlternativesForm(alternatives, _planPreference);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedPreference is null) return;
        _planPreference = dialog.SelectedPreference.Value;
        SaveSettings();
        InvalidatePlan();
        _planTimer.Stop();
        await PlanBatchAsync(true);
    }

    private void EditSelectedContract()
    {
        if (_grid.CurrentRow?.DataBoundItem is ContractScan scan) EditContract(scan);
    }

    private void EditContract(ContractScan scan)
    {
        using var editor = new ContractEditorForm(scan);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _execution = null;
        SaveSession();
        InvalidatePlan();
        RefreshGrid();
    }

    private void OpenTravelMap()
    {
        var window = new TravelMapForm(_travelTimes, _currentManifest);
        window.FormClosed += (_, _) => InvalidatePlan();
        window.Show(this);
    }

    private void OpenTradeIdeas()
    {
        // One trading window: a second one would watch the same screenshots and write the same database.
        if (_tradeWindow is { IsDisposed: false })
        {
            _tradeWindow.Activate();
            return;
        }
        var host = new TradeHost(_settings.Trade, _ocr, _ships, CurrentShip, () => _folder.Text.Trim(),
            folder => { _folder.Text = folder; SaveSettings(); }, SaveSettings, SendRunToTabletAsync, SetTradeTrackerEnabledAsync);
        var window = _tradeWindow = new TradeIdeasForm(CurrentShip(), AppContext.BaseDirectory, _tradeStation, host);
        window.FormClosed += (_, _) => { _tradeStation = window.Station; _tradeWindow = null; SaveSettings(); };
        window.Show(this);
    }

    private void OpenHistoryFolder()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "plan-history");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void ForgetProcessedImages()
    {
        if (MessageBox.Show($"Allow all {_imageRegistry.Count:N0} previously processed screenshots to be scanned again?", "Forget old images",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _imageRegistry.Clear();
        _status.Text = "Processed-image list cleared — old screenshots can be scanned again";
    }

    private void SavePlanHistory(string fingerprint, ManifestResult manifest, bool force)
    {
        if (manifest.Contracts.Count == 0 ||
            (manifest.HasKnownAvailability && manifest.HeadroomMinutes < 0 && !(force && manifest.TimerBudgetOverridden))) return;
        if (!force && fingerprint.Equals(_lastSavedPlanFingerprint, StringComparison.Ordinal)) return;
        try
        {
            var formatted = ManifestOptimizer.Format(manifest, CurrentShip(), _contracts, _planWarning);
            _lastHistoryFile = PlanHistoryStore.Save(AppContext.BaseDirectory, manifest, CurrentShip(), _contracts, formatted);
            _lastSavedPlanFingerprint = fingerprint;
            _status.Text += $" — saved {Path.GetFileName(_lastHistoryFile)}";
        }
        catch (Exception ex)
        {
            _planWarning = $"The route was created, but its JSON history could not be saved: {ex.Message}";
            RefreshManifest();
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
                _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonDefaults.Options()) ?? new();
        }
        catch { _settings = new(); }
        _settings.Trade ??= new();
        _folder.Text = _settings.ScreenshotFolder;
        _ship.SelectedItem = ShipCatalog.Find(_ships, _settings.ShipName)?.Name ?? "Custom";
        _capacity.Value = Math.Clamp(_settings.ShipCapacity, (int)_capacity.Minimum, (int)_capacity.Maximum);
        _apiBaseUrl = _settings.ApiBaseUrl;
        _tradeStation = _settings.TradeStation;
        ShowCargo();
    }

    private void SaveSettings()
    {
        // The same object stays in use because the trading window holds on to its Trade part.
        _settings.ScreenshotFolder = _folder.Text.Trim();
        _settings.ShipName = _ship.Text;
        _settings.ShipCapacity = (int)_capacity.Value;
        _settings.PlanPreference = _planPreference;
        _settings.ApiBaseUrl = _apiBaseUrl;
        _settings.TradeStation = _tradeStation;
        try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, JsonDefaults.Options())); } catch { }
    }

    private async Task RestoreAccountAsync()
    {
        _account.Enabled = false;
        try
        {
            var saved = AccountSessionVault.Account();
            if (saved is null)
            {
                _accountStatus.Text = "Signed out";
                return;
            }
            _apiBaseUrl = saved.ServerUrl;
            _authSession = saved.Session;
            SaveSettings();
            if (_authSession.RefreshTokenExpiresAtUtc <= DateTime.UtcNow)
            {
                SignOutLocally("Saved sign-in expired — sign in again");
                return;
            }

            try
            {
                await RefreshAccountAsync(new AccountClient(_apiBaseUrl), force: true);
                _accountStatus.Text = $"Signed in: {_authSession.Account.DisplayName}";
            }
            catch (AccountApiException ex) when (ex.StatusCode == 401)
            {
                SignOutLocally("Saved sign-in expired — sign in again");
                return;
            }
            catch (Exception ex) when (ex is AccountApiException or HttpRequestException or TaskCanceledException)
            {
                // Retain the encrypted refresh token. Pairing uses its own receiver token and can still resume;
                // account operations will retry the rotating refresh when they are next requested.
                _accountStatus.Text = $"Signed in offline: {_authSession.Account.DisplayName} — {ex.Message}";
            }

            _pairPhone.Enabled = true;
            RestorePairingSession();
            if (_settings.Trade.ShareTradeStats) _ = SetTradeTrackerEnabledAsync(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            SignOutLocally($"Saved sign-in could not be restored: {ex.Message}");
        }
        finally { _account.Enabled = true; }
    }

    private async Task<AuthSession> RefreshAccountAsync(AccountClient client, bool force = false)
    {
        await _accountRefreshGate.WaitAsync();
        try
        {
            var session = _authSession ?? throw new InvalidOperationException("Sign in to continue.");
            if (!force && session.AccessTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(1)) return session;
            var rotatedFrom = session.RefreshToken;
            try { session = await client.RefreshAsync(rotatedFrom, CancellationToken.None); }
            catch (AccountApiException ex) when (ex.StatusCode == 401)
            {
                SignOutLocally("Account session expired — sign in again");
                throw;
            }
            if (_authSession?.RefreshToken != rotatedFrom)
            {
                // Sign-out or another account won the race while the refresh request was in flight. Never
                // resurrect that session; revoke the newly rotated token as best effort.
                try { await client.LogoutAsync(session.RefreshToken, CancellationToken.None); } catch { }
                throw new AccountApiException("The account changed while its session was refreshing.", 409);
            }
            _authSession = session;
            var warning = PersistAccountSession();
            if (warning is not null) _accountStatus.Text = warning;
            return session;
        }
        finally { _accountRefreshGate.Release(); }
    }

    private string? PersistAccountSession()
    {
        if (_authSession is null) return null;
        try
        {
            AccountSessionVault.SaveAccount(_apiBaseUrl, _authSession);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or InvalidDataException)
        {
            return $"Windows could not securely remember this sign-in: {ex.Message}";
        }
    }

    private void RestorePairingSession()
    {
        var saved = AccountSessionVault.Pairing();
        if (saved is null || _authSession is null) return;
        if (saved.AccountId != _authSession.Account.Id || !AccountSessionVault.SameServer(saved.ServerUrl, _apiBaseUrl))
        {
            AccountSessionVault.ClearPairing();
            return;
        }

        var registration = new PairingRegistration { SessionId = saved.SessionId, ReceiverToken = saved.ReceiverToken };
        var client = new AccountClient(_apiBaseUrl);
        _receiverCancellation?.Cancel();
        _receiverCancellation = new CancellationTokenSource();
        _pairingClient = client;
        _pairingRegistration = registration;
        _tabletLinked = false;
        _lastPublishedMobilePlan = "";
        _pairPhone.Text = "Reconnecting phone…";
        _pairPhone.Enabled = false;
        _accountStatus.Text = $"Reconnecting tablet{(saved.DeviceName.Length > 0 ? $": {saved.DeviceName}" : "…")}";
        _ = ReceivePhoneCommandsAsync(client, registration, _receiverCancellation.Token, saved.LastCommandId);
    }

    private void PersistPairingSession(PairingRegistration registration, string deviceName, long lastCommandId)
    {
        if (!ReferenceEquals(_pairingRegistration, registration) || _authSession is null) return;
        try
        {
            AccountSessionVault.SavePairing(_apiBaseUrl, _authSession.Account.Id, registration, deviceName, lastCommandId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or InvalidDataException)
        {
            // Do not retain an older command cursor: replaying an already-recorded sale is worse than asking
            // the user to link again after the next restart.
            AccountSessionVault.ClearPairing();
            if (!IsDisposed) BeginInvoke(() => _accountStatus.Text = $"Tablet linked for this run, but secure reconnect could not be saved: {ex.Message}");
        }
    }

    private void SignOutLocally(string message = "Signed out")
    {
        _authSession = null;
        _receiverCancellation?.Cancel();
        _receiverCancellation = null;
        _pairingClient = null;
        _pairingRegistration = null;
        _tabletLinked = false;
        _tabletPlan = null;
        AccountSessionVault.ClearPairing();
        AccountSessionVault.ClearAccount();
        _accountStatus.Text = message;
        _account.Text = "Account";
        _pairPhone.Text = "Pair phone";
        _pairPhone.Enabled = false;
        UpdateSendTabletButton();
    }

    private async Task SendAnonymousLaunchAsync()
    {
        if (!_settings.Trade.ShareAnonymousUsage) return;
        try
        {
            var version = Application.ProductVersion.Split('+')[0];
            await new AccountClient(_apiBaseUrl).TrackLaunchAsync("windows", version[..Math.Min(version.Length, 32)], CancellationToken.None);
        }
        catch (Exception ex) when (ex is AccountApiException or HttpRequestException or TaskCanceledException or UriFormatException)
        {
            // Anonymous usage reporting must never delay startup or interrupt local planning.
        }
    }

    private async Task ManageAccountAsync()
    {
        if (_authSession is not null)
        {
            var choice = MessageBox.Show($"Signed in as {_authSession.Account.DisplayName} ({_authSession.Account.Email}).\n\nSign out?",
                "Freight Planner account", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes) return;
            var refreshToken = _authSession.RefreshToken;
            SignOutLocally();
            try { await new AccountClient(_apiBaseUrl).LogoutAsync(refreshToken, CancellationToken.None); }
            catch { /* Clear the local session even when the server is temporarily unavailable. */ }
            return;
        }

        using var dialog = new AccountForm(_apiBaseUrl);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Session is null) return;
        _apiBaseUrl = dialog.ApiBaseUrl;
        _authSession = dialog.Session;
        _account.Text = "Account";
        var warning = PersistAccountSession();
        _accountStatus.Text = $"Signed in: {_authSession.Account.DisplayName}" + (warning is null ? "" : $" — {warning}");
        _pairPhone.Enabled = true;
        SaveSettings();
        _ = SetTradeTrackerEnabledAsync(_settings.Trade.ShareTradeStats);
    }

    private async Task PairPhoneAsync()
    {
        if (_authSession is null) return;
        _pairPhone.Enabled = false;
        try
        {
            var client = new AccountClient(_apiBaseUrl);
            var session = await RefreshAccountAsync(client);
            var registration = await client.CreatePairingAsync(session.AccessToken, Environment.MachineName, CancellationToken.None);
            AccountSessionVault.ClearPairing();
            _receiverCancellation?.Cancel();
            _receiverCancellation = new CancellationTokenSource();
            _pairingClient = client;
            _pairingRegistration = registration;
            _tabletLinked = false;
            _lastPublishedMobilePlan = "";
            UpdateSendTabletButton();
            _ = ReceivePhoneCommandsAsync(client, registration, _receiverCancellation.Token, 0);
            _pairPhone.Text = "Waiting for phone…";
            _pairingForm?.Close();
            _pairingForm = new PairingForm(_apiBaseUrl, registration);
            _pairingForm.FormClosed += (sender, _) =>
            {
                if (!ReferenceEquals(_pairingForm, sender)) return;
                _pairingForm = null;
                // Closed by the user before a tablet connected: allow asking for a fresh code.
                if (_pairPhone.Text == "Waiting for phone…")
                {
                    _pairPhone.Text = "Pair phone";
                    _pairPhone.Enabled = _authSession is not null;
                }
            };
            _pairingForm.Show(this);
        }
        catch (Exception ex) when (ex is AccountApiException or HttpRequestException or TaskCanceledException)
        {
            _pairPhone.Text = "Pair phone";
            _pairPhone.Enabled = true;
            MessageBox.Show(this, ex.Message, "Pairing failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ReceivePhoneCommandsAsync(AccountClient client, PairingRegistration registration, CancellationToken cancellationToken,
        long lastCommandId)
    {
        var pairingAnnounced = false;
        try
        {
            var interrupted = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                CommandPoll poll;
                try
                {
                    poll = await client.PollCommandsAsync(registration.SessionId, registration.ReceiverToken,
                        lastCommandId, cancellationToken);
                }
                catch (AccountApiException ex) when (ex.StatusCode is 401 or 404)
                {
                    // The server keeps pairing sessions in memory, so a server restart drops this one for good.
                    if (!IsDisposed) BeginInvoke(() => DropTabletLink(registration, "Tablet link lost — click Pair phone to link again"));
                    return;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                           ex is AccountApiException or HttpRequestException or TaskCanceledException)
                {
                    // A network blip or rate limit must not end the link; keep trying.
                    interrupted = true;
                    if (!IsDisposed) BeginInvoke(() => _accountStatus.Text = "Tablet link interrupted — retrying…");
                    await Task.Delay(3000, cancellationToken);
                    continue;
                }
                if (interrupted && !IsDisposed)
                {
                    interrupted = false;
                    BeginInvoke(() => _accountStatus.Text = poll.Paired ? $"Tablet linked: {poll.DeviceName}" : "Waiting for the tablet to scan the QR code");
                }
                if (poll.Paired && !pairingAnnounced && !IsDisposed)
                {
                    pairingAnnounced = true;
                    PersistPairingSession(registration, poll.DeviceName, lastCommandId);
                    BeginInvoke(async () =>
                    {
                        _tabletLinked = true;
                        _pairPhone.Text = "Phone connected";
                        _pairingForm?.Close();
                        // A re-link gets back what the tablet already had; anything new only goes out through Send to tablet.
                        if (_tabletRun is not null) await PublishTradeRunAsync(_tabletRun);
                        else if (_tabletPlan is null) _accountStatus.Text = $"Tablet linked: {poll.DeviceName} — nothing sent yet";
                        else await PublishTabletPlanAsync(_tabletPlan, force: true, announce: true);
                        UpdateSendTabletButton();
                    });
                }
                foreach (var command in poll.Commands)
                {
                    if (command.Shortcut.StartsWith("FC1|", StringComparison.Ordinal) || command.Shortcut.StartsWith("FCT2|", StringComparison.Ordinal))
                    {
                        if (!IsDisposed) await ApplyTabletChecklistCommandOnUiAsync(command);
                    }
                    else if (command.CreatedAtUtc >= DateTime.UtcNow.AddSeconds(-20) && !IsDisposed)
                        BeginInvoke(() => _status.Text = $"Phone control ignored (game input disabled): {command.Label}");
                    lastCommandId = Math.Max(lastCommandId, command.Id);
                    PersistPairingSession(registration, poll.DeviceName, lastCommandId);
                }
                await Task.Delay(650, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed) BeginInvoke(() => DropTabletLink(registration, $"Tablet link lost: {ex.Message}"));
        }
    }

    private void DropTabletLink(PairingRegistration registration, string message)
    {
        // A newer pairing may already have replaced the one that failed.
        if (!ReferenceEquals(_pairingRegistration, registration)) return;
        _pairingClient = null;
        _pairingRegistration = null;
        _tabletLinked = false;
        AccountSessionVault.ClearPairing();
        _pairingForm?.Close();
        _pairPhone.Text = "Pair phone";
        _pairPhone.Enabled = _authSession is not null;
        _accountStatus.Text = message;
        UpdateSendTabletButton();
    }

    private async Task ApplyTabletChecklistCommandAsync(RemoteCommand command)
    {
        if (_tabletRun is { } run)
        {
            await ApplyTradeRunCommandAsync(run, command);
            return;
        }
        if (_tabletPlan is not { } sent) return;
        var parts = command.Shortcut.Split('|');
        if (parts.Length != 6 || parts[0] != "FC1" ||
            !long.TryParse(parts[1], out var revision) || !int.TryParse(parts[2], out var stopIndex) ||
            !int.TryParse(parts[4], out var itemIndex) || !int.TryParse(parts[5], out var state))
        {
            _status.Text = "Tablet checklist update was malformed and was ignored";
            return;
        }
        // Ticked on the previous plan just before the tablet received this one.
        if (revision < sent.FirstRevision) return;

        var guide = sent.Stops;
        if (stopIndex < 0 || stopIndex >= guide.Count) return;
        var kind = parts[3].ToUpperInvariant();
        if (kind is not ("D" or "P" or "K" or "S")) return;
        var done = state == 1;
        if (IsFor(_execution, sent)) sent.Progress = _execution;
        else
        {
            sent.Progress ??= new ExecutionProgress
            {
                PlanFingerprint = sent.Fingerprint,
                ShipName = sent.ShipName,
                Route = sent.Route.ToList()
            };
            // Also the PC's active run, unless the PC has since moved on to a different plan of its own.
            if (_execution is null || (_currentManifest is { Contracts.Count: > 0 } current &&
                                       ContractIdentity.PlanFingerprint(current).Equals(sent.Fingerprint, StringComparison.OrdinalIgnoreCase)))
                _execution = sent.Progress;
        }

        var progress = sent.Progress!;
        var timestamp = command.CreatedAtUtc == default ? DateTime.UtcNow : command.CreatedAtUtc;
        var timingNotes = new List<string>();
        var expected = TabletChecklistKeys(stopIndex, guide[stopIndex]);
        if (kind == "S")
        {
            foreach (var key in expected) progress.TabletChecklist[key] = done;
        }
        else
        {
            var key = $"{stopIndex}|{kind}|{itemIndex}";
            if (!expected.Contains(key, StringComparer.OrdinalIgnoreCase)) return;
            progress.TabletChecklist[key] = done;
        }

        if (done && !progress.StopStartedAtUtc.ContainsKey(stopIndex))
        {
            progress.StopStartedAtUtc[stopIndex] = timestamp;
            if (stopIndex > 0 && progress.StopCompletedAtUtc.TryGetValue(stopIndex - 1, out var previousCompleted) &&
                !progress.RecordedTravelArrivals.Contains(stopIndex))
            {
                var elapsed = timestamp - previousCompleted;
                if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(4))
                {
                    var minutes = Math.Max(1, (int)Math.Round(elapsed.TotalMinutes));
                    var learnedLeg = _travelTimes.RecordObservation(guide[stopIndex - 1].Location, guide[stopIndex].Location, minutes, sent.ShipName);
                    progress.RecordedTravelArrivals.Add(stopIndex);
                    timingNotes.Add($"flight {minutes}m (avg {learnedLeg.Minutes}m)");
                }
            }
        }

        var stopComplete = expected.Count == 0 ? kind == "S" && done : expected.All(key => progress.TabletChecklist.GetValueOrDefault(key));
        if (stopComplete)
        {
            progress.StopCompletedAtUtc[stopIndex] = timestamp;
            if (progress.StopStartedAtUtc.TryGetValue(stopIndex, out var started) &&
                timestamp > started && !progress.RecordedHandlingStops.Contains(stopIndex))
            {
                var handling = _travelTimes.RecordHandlingObservation(guide[stopIndex].Location, timestamp - started, sent.ShipName);
                progress.RecordedHandlingStops.Add(stopIndex);
                timingNotes.Add($"stop work {(timestamp - started).TotalMinutes:N1}m (avg {handling.Minutes}m)");
            }
        }
        else if (!done)
        {
            progress.StopCompletedAtUtc.Remove(stopIndex);
        }

        progress.CurrentStopIndex = Math.Max(progress.CurrentStopIndex, stopIndex);
        progress.CurrentStopCompleted = progress.CurrentStopIndex == stopIndex && stopComplete;
        SaveSession();

        var allKeys = guide.SelectMany((step, index) => TabletChecklistKeys(index, step)).ToList();
        var completed = allKeys.Count(key => progress.TabletChecklist.GetValueOrDefault(key));
        _status.Text = $"Tablet checklist synced — {completed}/{allKeys.Count} cargo action(s) complete" +
                       (timingNotes.Count == 0 ? "" : $" — learned {string.Join(", ", timingNotes)}");
        _accountStatus.Text = $"Tablet progress: stop {stopIndex + 1}/{guide.Count}";
        RefreshManifest();
        _ = PublishTabletPlanAsync(sent);
    }

    private Task ApplyTabletChecklistCommandOnUiAsync(RemoteCommand command)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(async () =>
        {
            try
            {
                await ApplyTabletChecklistCommandAsync(command);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }

    private static bool IsFor(ExecutionProgress? progress, TabletPlan plan) =>
        progress?.PlanFingerprint.Equals(plan.Fingerprint, StringComparison.OrdinalIgnoreCase) == true;

    private static List<string> TabletChecklistKeys(int stopIndex, MobilePlanStop stop)
    {
        var result = new List<string>();
        for (var i = 0; i < stop.Deliveries.Count; i++) result.Add($"{stopIndex}|D|{i}");
        for (var i = 0; i < stop.Pickups.Count; i++) result.Add($"{stopIndex}|P|{i}");
        for (var i = 0; i < stop.Packing.Count; i++) result.Add($"{stopIndex}|K|{i}");
        return result;
    }

    private async Task SendPlanToTabletAsync()
    {
        if (_currentManifest is null || _currentManifest.Contracts.Count == 0)
        {
            MessageBox.Show(this, "Create a flight plan before sending it to the tablet.", "No active plan",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_authSession is null)
        {
            MessageBox.Show(this, "Sign in first: click Account, then Pair phone, and scan the QR code with the tablet.",
                "Not signed in", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_pairingClient is null || _pairingRegistration is null || !_tabletLinked)
        {
            // Without this check the plan would be "sent" to a session no tablet is listening on.
            MessageBox.Show(this,
                "No tablet is linked to this session yet.\n\nClick Pair phone and scan the QR code with the tablet. " +
                "If the tablet still shows an older link, tap RELINK PC on it first, then SCAN QR.\n\n" +
                "The link has to be made again each time this app is started.",
                "Tablet not linked", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _sendTablet.Enabled = false;
        _sendTablet.Text = "Sending…";
        try
        {
            var plan = BuildTabletPlan(_currentManifest, CurrentShip());
            // The tablet clears its ticks when the summary line changes, and the trip estimate in it drifts.
            // So an unchanged checklist is re-sent exactly as the tablet already has it.
            if (_tabletPlan is { } previous && previous.Key == plan.Key) plan = previous;
            await PublishTabletPlanAsync(plan, force: true, announce: true);
        }
        finally { UpdateSendTabletButton(); }
    }

    private void UpdateSendTabletButton()
    {
        var manifest = _currentManifest;
        // Enabled whenever there is a plan, so clicking it can explain what is still missing for the tablet.
        _sendTablet.Enabled = manifest is { Contracts.Count: > 0 };
        _sendTablet.Text = _tabletLinked && _tabletPlan is { } sent && manifest is { Contracts.Count: > 0 } &&
                           BuildTabletPlan(manifest, CurrentShip()).Key == sent.Key
            ? "Sent to tablet ✓"
            : "Send to tablet";
    }

    private TabletPlan BuildTabletPlan(ManifestResult manifest, ShipProfile ship)
    {
        var title = manifest.TimerBudgetOverridden ? "TIMER OVERRIDE FLIGHT PLAN"
            : manifest.HasUnverifiedValues ? "UNVERIFIED FLIGHT PLAN"
            : "CURRENT FLIGHT PLAN";
        var summary = $"{ship.Name} • {manifest.TotalScu:N0}/{ship.CapacityScu:N0} SCU • " +
                      (manifest.UnknownRewardCount > 0 ? $"{manifest.TotalReward:N0} known aUEC" : $"{manifest.TotalReward:N0} aUEC") +
                      $" • ~{manifest.EstimatedTripMinutes:N0} min";
        var stops = ManifestOptimizer.BuildRouteGuide(manifest.LocalRoute, manifest, _travelTimes)
            .Select(x => new MobilePlanStop(x.Number, x.Location, x.TravelMinutesFromPrevious,
                x.Deliveries, x.Pickups, x.Packing)).ToArray();
        // Travel estimates change as flights are timed, so they do not count as a different plan.
        var key = JsonSerializer.Serialize(new { title, ship, stops = stops.Select(x => new { x.Location, x.Deliveries, x.Pickups, x.Packing }) });
        return new TabletPlan(key, ContractIdentity.PlanFingerprint(manifest), ship.Name, manifest.Contracts.Count,
            title, summary, manifest.LocalRoute.ToList(), stops);
    }

    // The only way anything reaches the tablet. Send to tablet forces a plan out; without force this only
    // moves the current-stop marker of the plan the tablet already has.
    private async Task<bool> PublishTabletPlanAsync(TabletPlan plan, bool force = false, bool announce = false)
    {
        var client = _pairingClient;
        var registration = _pairingRegistration;
        if (client is null || registration is null) return false;

        await _mobilePlanPublishGate.WaitAsync();
        try
        {
            // A progress update that waited behind a newer Send must not put the older plan back.
            if (!force && !ReferenceEquals(plan, _tabletPlan)) return false;
            var progress = IsFor(_execution, plan) ? _execution : plan.Progress;
            var currentStopIndex = Math.Clamp(progress?.CurrentStopIndex ?? 0, 0, Math.Max(0, plan.Stops.Count - 1));
            var currentStopCompleted = progress?.CurrentStopCompleted ?? false;
            var contentFingerprint = JsonSerializer.Serialize(new { plan.Title, plan.Summary, currentStopIndex, currentStopCompleted, plan.Stops }, JsonDefaults.Options());
            if (!force && contentFingerprint == _lastPublishedMobilePlan) return true;
            var snapshot = new MobilePlanSnapshot(++_mobilePlanRevision, DateTime.UtcNow, plan.Title, plan.Summary,
                currentStopIndex, currentStopCompleted, plan.Stops);
            if (plan.FirstRevision == 0) plan.FirstRevision = snapshot.Revision;
            await client.PublishPlanAsync(registration.SessionId, registration.ReceiverToken, snapshot, CancellationToken.None);
            _lastPublishedMobilePlan = contentFingerprint;
            _tabletPlan = plan;
            _tabletRun = null;
            if (announce && !IsDisposed)
            {
                _accountStatus.Text = $"Tablet synced: {plan.Stops.Count} stops at {DateTime.Now:HH:mm:ss}";
                _status.Text = $"Flight plan sent to tablet — {plan.Jobs} job(s), {plan.Stops.Count} route stop(s)";
            }
            return true;
        }
        catch (Exception ex) when (ex is AccountApiException or HttpRequestException or TaskCanceledException)
        {
            if (!IsDisposed)
            {
                _accountStatus.Text = $"Plan sync failed: {ex.Message}";
                if (announce)
                    MessageBox.Show(this, ex.Message, "Could not send plan to tablet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;
        }
        finally { _mobilePlanPublishGate.Release(); }
    }

    // Trade ideas hands a run over here. Windows always opens its monitor; a linked tablet receives the
    // same run as a second, synchronized surface.
    private async Task<string> SendRunToTabletAsync(TradeRun run)
    {
        if (_activeTradeRun is { IsCompleted: false } active && !ReferenceEquals(active, run) &&
            MessageBox.Show(this, $"Replace the active {active.Commodity} run with this {run.Commodity} run? The existing journal row is kept.",
                "Start another trade run", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return "The existing trade run is still active.";

        _activeTradeRun = _tabletRun = run;
        _tabletPlan = null;
        _tradeMonitor.Enabled = true;
        SaveSession();
        OpenTradeRunMonitor();

        if (_authSession is null || _pairingClient is null || _pairingRegistration is null || !_tabletLinked)
        {
            _status.Text = $"Trade run started on Windows — {run.Scu:N0} SCU {run.Commodity}, {run.Origin} to {run.Destination}";
            return "The Windows trade monitor is running. Link a tablet if you also want the checklist there.";
        }
        var problem = await PublishTradeRunAsync(run);
        if (problem is not null) return $"The Windows trade monitor is running, but the run could not be sent to the tablet: {problem}";
        _status.Text = $"Trade run sent to tablet — {run.Scu:N0} SCU {run.Commodity}, {run.Origin} to {run.Destination}";
        return $"Windows and tablet: {run.Scu:N0} SCU {run.Commodity} to {run.Destination}{(run.DestinationDetail.Length > 0 ? $" ({run.DestinationDetail})" : "")}. " +
               "The clock is running; tick the steps on either screen.";
    }

    private void OpenTradeRunMonitor()
    {
        if (_activeTradeRun is not { } run) return;
        if (_tradeRunMonitor is { IsDisposed: false } existing && ReferenceEquals(existing.Run, run))
        {
            existing.Activate();
            return;
        }
        _tradeRunMonitor?.Close();
        var monitor = new TradeRunMonitorForm(run, (step, done) => ApplyWindowsTradeStepAsync(run, step, done));
        _tradeRunMonitor = monitor;
        monitor.FormClosed += (_, _) => { if (ReferenceEquals(_tradeRunMonitor, monitor)) _tradeRunMonitor = null; };
        monitor.Show(this);
    }

    // Puts a trade run on the tablet, or moves its stop marker on. Null when it went out, else why not.
    private async Task<string?> PublishTradeRunAsync(TradeRun run)
    {
        var client = _pairingClient;
        var registration = _pairingRegistration;
        if (client is null || registration is null) return "the tablet link is gone";

        await _mobilePlanPublishGate.WaitAsync();
        try
        {
            var snapshot = run.ToSnapshot(++_mobilePlanRevision, DateTime.UtcNow);
            if (run.FirstRevision == 0) run.FirstRevision = snapshot.Revision;
            await client.PublishPlanAsync(registration.SessionId, registration.ReceiverToken, snapshot, CancellationToken.None);
            _tabletRun = run;
            _tabletPlan = null;
            _lastPublishedMobilePlan = "";
            if (!IsDisposed)
            {
                UpdateSendTabletButton();
                _accountStatus.Text = $"Tablet synced: trade run at {DateTime.Now:HH:mm:ss}";
            }
            return null;
        }
        catch (Exception ex) when (ex is AccountApiException or HttpRequestException or TaskCanceledException)
        {
            if (!IsDisposed) _accountStatus.Text = $"Trade run sync failed: {ex.Message}";
            return ex.Message;
        }
        finally { _mobilePlanPublishGate.Release(); }
    }

    // A step ticked or unticked on the tablet. The relay stamps each tick with its own clock, and that time is kept.
    private async Task ApplyTradeRunCommandAsync(TradeRun run, RemoteCommand command)
    {
        var parts = command.Shortcut.Split('|');
        var timestamp = command.CreatedAtUtc == default ? DateTime.UtcNow : command.CreatedAtUtc.ToUniversalTime();
        if (parts.Length >= 5 && parts[0] == "FCT2" && long.TryParse(parts[1], out var actualRevision))
        {
            if (actualRevision < run.FirstRevision) return;
            try
            {
                if (parts.Length == 5 && parts[2] == "B" && int.TryParse(parts[3], out var boughtScu) &&
                    double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var boughtPrice))
                {
                    run.RecordPurchase(boughtScu, boughtPrice, timestamp);
                    await FinishTradeRunChangeAsync(run, TradeRun.Bought, true);
                    return;
                }
                if (parts.Length == 6 && parts[2] == "S" && int.TryParse(parts[3], out var terminalId) &&
                    int.TryParse(parts[4], out var soldScu) && double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var soldPrice))
                {
                    var option = run.SaleOptions.FirstOrDefault(x => x.TerminalId == terminalId);
                    if (option is null) throw new InvalidOperationException("The selected buyer is not part of this run's current suggestions.");
                    run.RecordSale(option.TerminalId, option.TerminalName, soldScu, soldPrice, timestamp);
                    await FinishTradeRunChangeAsync(run, TradeRun.Sold, true);
                    return;
                }
            }
            catch (InvalidOperationException ex)
            {
                _status.Text = $"Tablet trade update was rejected: {ex.Message}";
                await PublishTradeRunAsync(run);
                return;
            }
            _status.Text = "Tablet trade amount was malformed and was ignored";
            return;
        }
        if (parts.Length != 6 || parts[0] != "FC1" ||
            !long.TryParse(parts[1], out var revision) || !int.TryParse(parts[2], out var stopIndex) ||
            !int.TryParse(parts[4], out var itemIndex) || !int.TryParse(parts[5], out var state))
        {
            _status.Text = "Tablet checklist update was malformed and was ignored";
            return;
        }
        // Ticked on whatever the tablet showed just before it received this run.
        if (revision < run.FirstRevision) return;
        var kind = parts[3].ToUpperInvariant();
        var done = state == 1;
        var step = run.Steps.FirstOrDefault(x => x.Stop == stopIndex && x.Kind == kind && x.Item == itemIndex);
        if (!run.Apply(stopIndex, kind, itemIndex, done, timestamp)) return;

        await FinishTradeRunChangeAsync(run, step?.Id ?? "", done);
    }

    private async Task<string> ApplyWindowsTradeStepAsync(TradeRun run, TradeStep step, bool done)
    {
        if (done && step.Id == TradeRun.Bought)
        {
            using var form = new TradeActualsForm(run, purchase: true);
            if (form.ShowDialog(this) != DialogResult.OK) return run.Describe();
            run.RecordPurchase(form.Scu, form.Price, DateTime.UtcNow);
            return await FinishTradeRunChangeAsync(run, step.Id, true);
        }
        if (done && step.Id == TradeRun.Sold)
        {
            using var form = new TradeActualsForm(run, purchase: false);
            if (form.ShowDialog(this) != DialogResult.OK) return run.Describe();
            var terminal = form.Terminal ?? throw new InvalidOperationException("Choose a buyer.");
            run.RecordSale(terminal.TerminalId, terminal.TerminalName, form.Scu, form.Price, DateTime.UtcNow);
            return await FinishTradeRunChangeAsync(run, step.Id, true);
        }
        var place = step.Key.Split('|');
        if (place.Length != 3 || !int.TryParse(place[0], out var stop) || !int.TryParse(place[2], out var item) ||
            !run.Apply(stop, place[1], item, done, DateTime.UtcNow))
            return run.Describe();
        return await FinishTradeRunChangeAsync(run, step.Id, done);
    }

    private async Task<string> FinishTradeRunChangeAsync(TradeRun run, string changedStep, bool done)
    {
        var message = run.Describe();
        TradeRecord? record = null;

        try
        {
            // Nothing goes into the journal before the cargo is bought.
            if (run.TradeId > 0 || run.IsBought)
            {
                var store = new MarketStore(AppContext.BaseDirectory);
                record = run.TradeId > 0 ? store.Trades().FirstOrDefault(x => x.Id == run.TradeId) : null;
                record ??= new TradeRecord();
                run.Fill(record);
                store.Save(record);
                run.TradeId = record.Id;
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            message += $" The trading journal could not be written: {ex.Message}";
        }

        // The journal is written first. Recovery can therefore merge a newer committed row into this snapshot
        // if Windows stops between the two writes, without adding the purchase or sale twice.
        SaveSession();

        // Publish every accepted change. Android only redraws a changed revision, so the full timestamp map
        // is also the acknowledgement that makes an untick on either device visible on the other.
        if (ReferenceEquals(_tabletRun, run) && _pairingClient is not null && _pairingRegistration is not null && _tabletLinked)
        {
            var problem = await PublishTradeRunAsync(run);
            if (problem is not null) message += $" Tablet sync failed: {problem}.";
        }

        // A second surface may have unticked this checkpoint while a tablet publish was in flight.
        if (done && record is not null)
        {
            if (changedStep == TradeRun.Bought)
                message += await SyncTradeCheckpointAsync(record.Id, buy: true);
            else if (changedStep == TradeRun.Sold)
            {
                // If sync was enabled after the purchase, catch that side up before adding the sale.
                if (run.IsBought) message += await SyncTradeCheckpointAsync(record.Id, buy: true);
                message += await SyncTradeCheckpointAsync(record.Id, buy: false);
                if (run.IsCompleted) message += await SyncCommunityTradeAsync(record.Id);
            }
        }

        _status.Text = message;
        _accountStatus.Text = _tabletLinked ? $"Tablet progress: {run.Done.Count} step(s) ticked" : $"Trade progress: {run.Done.Count} step(s) ticked";
        _tradeRunMonitor?.RefreshFromRun(message);
        if (_tradeWindow is { IsDisposed: false }) _tradeWindow.ShowTabletRun(message);
        return message;
    }

    private async Task<string> SyncTradeCheckpointAsync(long tradeId, bool buy)
    {
        if (!_settings.Trade.SyncTradesToUex) return "";
        await _uexTradeSyncGate.WaitAsync();
        try
        {
            var store = new MarketStore(AppContext.BaseDirectory);
            var trade = store.Trades().FirstOrDefault(x => x.Id == tradeId);
            if (trade is null) return " UEX trade sync could not find the journal row.";
            var credentials = CredentialVault.Uex();
            if (credentials is null)
            {
                const string detail = "UEX app token and secret key are not both stored.";
                if (buy) store.SetTradeSubmission(tradeId, true, SubmissionState.Failed, detail, null);
                else foreach (var sale in trade.Sales.Where(x => x.UexSubmission != SubmissionState.Submitted)) store.SetSaleSubmission(sale.Id, SubmissionState.Failed, detail, null);
                return $" {detail}";
            }

            if (buy)
            {
                if (trade.UexBuyId is not null || trade.UexBuySubmission == SubmissionState.Submitted)
                    return trade.UexBuyId is { } known ? $" UEX already has the purchase (trade {known})." : " UEX already has the purchase.";
                if (trade.PriceBuy <= 0 || trade.OriginId <= 0)
                {
                    const string detail = "The purchase has no valid terminal or price for UEX.";
                    store.SetTradeSubmission(tradeId, true, SubmissionState.Failed, detail, null);
                    return $" {detail}";
                }

                UexTradeResult result;
                try
                {
                    result = await new UexClient(AppContext.BaseDirectory).AddTradeAsync(new UexTradeSubmission
                    {
                        IdTerminal = trade.OriginId, IdCommodity = trade.CommodityId, Operation = "buy",
                        Scu = trade.Scu, Price = trade.PriceBuy, IsProduction = 1
                    }, credentials, CancellationToken.None);
                }
                catch (UexException ex)
                {
                    store.SetTradeSubmission(tradeId, true, SubmissionState.Failed, ex.Message, null);
                    return $" UEX trade sync failed: {ex.Message}";
                }

                if (!result.Ok || result.TradeId is null)
                {
                    var detail = result.Ok ? "UEX accepted the trade but did not return its ID, so it was not marked synchronized." : result.Message;
                    store.SetTradeSubmission(tradeId, true, SubmissionState.Failed, detail, null);
                    return $" UEX trade sync failed: {detail}";
                }
                store.SetTradeSubmission(tradeId, true, SubmissionState.Submitted, $"UEX trade {result.TradeId}", result.TradeId);
                return $" Purchase added to UEX trade history (trade {result.TradeId}).";
            }

            var messages = new List<string>();
            foreach (var sale in trade.Sales.Where(x => x.UexId is null && x.UexSubmission != SubmissionState.Submitted))
            {
                try
                {
                    var result = await new UexClient(AppContext.BaseDirectory).AddTradeAsync(new UexTradeSubmission
                    {
                        IdTerminal = sale.TerminalId, IdCommodity = trade.CommodityId, Operation = "sell",
                        Scu = sale.Scu, Price = sale.PricePerScu, IsProduction = 1
                    }, credentials, CancellationToken.None);
                    if (!result.Ok || result.TradeId is null)
                    {
                        var detail = result.Ok ? "UEX did not return a trade ID." : result.Message;
                        store.SetSaleSubmission(sale.Id, SubmissionState.Failed, detail, null);
                        messages.Add($"UEX sale sync failed: {detail}");
                    }
                    else
                    {
                        store.SetSaleSubmission(sale.Id, SubmissionState.Submitted, $"UEX trade {result.TradeId}", result.TradeId);
                        messages.Add($"{sale.Scu:N0} SCU sale added to UEX (trade {result.TradeId})");
                    }
                }
                catch (UexException ex)
                {
                    store.SetSaleSubmission(sale.Id, SubmissionState.Failed, ex.Message, null);
                    messages.Add($"UEX sale sync failed: {ex.Message}");
                }
            }
            return messages.Count == 0 ? " UEX already has every sale." : " " + string.Join("; ", messages) + ".";
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            return $" UEX trade sync state could not be saved: {ex.Message}";
        }
        finally { _uexTradeSyncGate.Release(); }
    }

    private async Task<string> SetTradeTrackerEnabledAsync(bool enabled)
    {
        if (_authSession is null)
            return enabled ? "Saved locally. Sign in to contribute completed runs." : "Website statistics are off on this PC.";
        try
        {
            var client = new AccountClient(_apiBaseUrl);
            var session = await RefreshAccountAsync(client);
            await client.SetTradeProfileAsync(session.AccessToken, enabled, CancellationToken.None);
            if (!enabled)
                return "Leaderboard participation is off; earlier contributions are hidden from public totals.";

            var backfill = await SyncPendingCommunityTradesAsync();
            return $"Participating as {_authSession.Account.DisplayName}; completed runs will appear in the public totals.{backfill}";
        }
        catch (Exception ex) when (ex is AccountApiException or HttpRequestException or TaskCanceledException)
        {
            return $"The preference is saved locally, but the server could not be updated: {ex.Message}";
        }
    }

    private async Task<string> SyncCommunityTradeAsync(long tradeId)
    {
        if (!_settings.Trade.ShareTradeStats) return "";
        MarketStore store;
        TradeRecord? trade;
        try
        {
            store = new MarketStore(AppContext.BaseDirectory);
            trade = store.Trades().FirstOrDefault(x => x.Id == tradeId);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            return $" Community tracker state could not be read: {ex.Message}";
        }
        if (trade is null || !trade.IsCompleted) return " Community tracker skipped an incomplete journal row.";
        if (trade.TrackerSubmission == SubmissionState.Submitted) return " Community tracker already has this run.";
        if (_authSession is null)
        {
            const string detail = "Sign in to a Freight Planner account to contribute trade statistics.";
            store.SetTrackerSubmission(trade.Id, SubmissionState.Failed, detail);
            return $" {detail}";
        }
        if (!Guid.TryParse(trade.TrackerId, out var trackerId))
        {
            const string detail = "The journal row has no valid community-tracker ID.";
            store.SetTrackerSubmission(trade.Id, SubmissionState.Failed, detail);
            return $" {detail}";
        }

        try
        {
            var client = new AccountClient(_apiBaseUrl);
            var session = await RefreshAccountAsync(client);
            await client.UploadCompletedTradeAsync(session.AccessToken, new CommunityTradeUpload
            {
                TradeId = trackerId, CommodityId = trade.CommodityId, CommodityName = trade.CommodityName,
                OriginId = trade.OriginId, OriginName = trade.OriginName, DestinationId = trade.DestinationId,
                DestinationName = trade.DestinationName, Ship = trade.Ship, Scu = trade.Scu, PriceBuy = trade.PriceBuy,
                PriceSell = trade.PriceSell!.Value, OperatingCosts = trade.OperatingCosts ?? 0,
                BoughtUtc = trade.BoughtUtc, SoldUtc = trade.SoldUtc!.Value, LeaderboardOptIn = true
            }, CancellationToken.None);
            store.SetTrackerSubmission(trade.Id, SubmissionState.Submitted, "Stored on the community trade tracker");
            return " Completed run added to the community trade tracker.";
        }
        catch (Exception ex) when (ex is AccountApiException or HttpRequestException or TaskCanceledException)
        {
            store.SetTrackerSubmission(trade.Id, SubmissionState.Failed, ex.Message);
            return $" Community trade upload failed: {ex.Message}";
        }
    }

    private async Task<string> SyncPendingCommunityTradesAsync()
    {
        List<long> pending;
        try
        {
            pending = new MarketStore(AppContext.BaseDirectory).Trades()
                .Where(x => x.IsCompleted && x.TrackerSubmission != SubmissionState.Submitted)
                .Select(x => x.Id)
                .ToList();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            return $" Existing journal rows could not be checked: {ex.Message}";
        }

        if (pending.Count == 0) return "";
        foreach (var tradeId in pending)
            await SyncCommunityTradeAsync(tradeId);

        try
        {
            var submitted = new MarketStore(AppContext.BaseDirectory).Trades()
                .Count(x => pending.Contains(x.Id) && x.TrackerSubmission == SubmissionState.Submitted);
            var failed = pending.Count - submitted;
            return failed == 0
                ? $" Uploaded {submitted:N0} existing completed run{(submitted == 1 ? "" : "s")}."
                : $" Uploaded {submitted:N0} existing run{(submitted == 1 ? "" : "s")}; {failed:N0} could not be uploaded and will be retried later.";
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            return $" Existing journal uploads finished, but their saved status could not be re-read: {ex.Message}";
        }
    }

    private void RestoreSession()
    {
        _planPreference = _settings.PlanPreference;
        var snapshot = _sessionStore.Load();
        foreach (var storedContract in snapshot.Contracts)
        {
            var contract = MissionParser.ReparseStored(storedContract);
            if (string.IsNullOrWhiteSpace(contract.SourceFile)) continue;
            var duplicate = _contracts.FirstOrDefault(x => ContractIdentity.SameMission(x, contract));
            if (duplicate is not null)
            {
                ContractIdentity.MergeBest(duplicate, contract);
                continue;
            }
            _contracts.Add(contract);
        }
        _execution = snapshot.Execution;
        TradeRun? restoredRun = null;
        var savedRunWasInvalid = false;
        if (snapshot.ActiveTradeRun is { } saved)
        {
            try
            {
                restoredRun = TradeRun.Restore(saved);
                if (restoredRun.TradeId > 0)
                {
                    var journal = new MarketStore(AppContext.BaseDirectory).Trades().FirstOrDefault(x => x.Id == restoredRun.TradeId);
                    if (journal is not null) restoredRun.MergeJournal(journal);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                savedRunWasInvalid = true;
                _status.Text = $"The saved trade run could not be read: {ex.Message}";
            }
        }

        // An atomic schema-2 session normally covers every run. This fallback upgrades an older, missing or
        // corrupt session whose synchronized journal row was already committed before Windows stopped.
        if (restoredRun is null && (snapshot.SchemaVersion < 2 || savedRunWasInvalid))
        {
            try
            {
                var journal = new MarketStore(AppContext.BaseDirectory).Trades()
                    .Where(x => !x.IsCompleted && !string.IsNullOrWhiteSpace(x.StepTimes) &&
                                x.Notes.Contains("synchronized trade monitor", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.BoughtUtc)
                    .FirstOrDefault();
                if (journal is not null) restoredRun = TradeRun.Recover(journal);
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                _status.Text = $"The saved trade run could not be recovered: {ex.Message}";
            }
        }
        if (restoredRun is { IsCompleted: false })
        {
            _activeTradeRun = _tabletRun = restoredRun;
            _tradeMonitor.Enabled = true;
        }
        if (_contracts.Count > 0)
        {
            RefreshGrid();
            _status.Text = $"Restored {_contracts.Count:N0} contract(s) from the active session";
        }
    }

    private void SaveSession()
    {
        try { _sessionStore.Save(_contracts, _execution, _activeTradeRun); } catch { }
    }

    private Color UrgencyColor(ContractScan scan)
    {
        if (scan.AvailabilityMinutes is not > 0) return Color.Honeydew;
        _travelTimes.ActiveShipName = CurrentShip().Name;
        var minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
        var key = $"{CurrentShip().Name}|{CurrentShip().CapacityScu}|{_travelTimes.Revision}|{minute}|{scan.AvailabilityMinutes}|{scan.CapturedAtUtc.Ticks}|{scan.TotalScu}";
        if (_urgencyColors.TryGetValue(scan, out var cached) && cached.Key == key) return cached.Color;
        var required = ManifestOptimizer.EstimateSingleContractMinutes(scan, _travelTimes) + 10;
        var headroom = scan.RemainingMinutes(DateTime.UtcNow) - required;
        var color = headroom < 0 ? Color.FromArgb(255, 205, 205) : headroom < 15 ? Color.LemonChiffon : Color.Honeydew;
        _urgencyColors[scan] = (key, color);
        return color;
    }
}
