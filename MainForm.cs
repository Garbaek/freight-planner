using System.Collections.Concurrent;
using System.Diagnostics;
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
    private CancellationTokenSource? _receiverCancellation;
    private AccountClient? _pairingClient;
    private PairingRegistration? _pairingRegistration;
    private readonly SemaphoreSlim _mobilePlanPublishGate = new(1, 1);
    private string _lastPublishedMobilePlan = "";
    private long _mobilePlanRevision;

    private PairingForm? _pairingForm;
    private bool _tabletLinked;

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
    private readonly Button _watch = new() { Text = "Start watching", AutoSize = true };
    private readonly Button _scanRecent = new() { Text = "Scan recent screenshots", AutoSize = true };
    private readonly Button _testOld = new() { Text = "Test old screenshots", AutoSize = true };
    private readonly Button _planNow = new() { Text = "Plan suggested run", AutoSize = true };
    private readonly Button _openRoute = new() { Text = "Open route view", AutoSize = true, Enabled = false };
    private readonly Button _execute = new() { Text = "Execute route", AutoSize = true, Enabled = false };
    private readonly Button _comparePlans = new() { Text = "Compare plans", AutoSize = true, Enabled = false };
    private readonly Button _editContract = new() { Text = "Edit / inspect contract", AutoSize = true, Enabled = false };
    private readonly Button _travelMap = new() { Text = "Travel map & times", AutoSize = true };
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
        RestoreSession();
        _status.Text = _ocr is null ? "Local OCR unavailable" : _contracts.Count > 0
            ? $"Local OCR ready — restored {_contracts.Count:N0} active contract(s)"
            : $"Local OCR ready — {_ocr.EngineDescription}";
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _planTimer.Tick += async (_, _) => { _planTimer.Stop(); await PlanBatchAsync(false); };
        _countdownTimer.Tick += (_, _) => RefreshManifest(countdownTick: true);
        _countdownTimer.Start();
        Shown += async (_, _) =>
        {
            if (!Directory.Exists(_folder.Text)) await FindScreenshotFolderAsync(announce: false);
            if (_contracts.Count > 0 && _currentManifest is null) await PlanBatchAsync(false);
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
        AddRow(settings, 0, "Screenshots", folderRow, "Ship", _ship);
        AddRow(settings, 1, "Status", _status, "Capacity", _capacity);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.AddRange([_watch, _scanRecent, _testOld, _planNow, _comparePlans, _openRoute, _execute, _editContract, _travelMap, _openHistory, _forgetImages, _clear, _account, _pairPhone, _sendTablet, _accountStatus]);
        settings.Controls.Add(buttons, 1, 2);
        settings.SetColumnSpan(buttons, 3);

        _ship.Items.AddRange(_ships.Select(x => x.Name).ToArray());
        _ship.SelectedIndexChanged += (_, _) =>
        {
            var selected = _ships.FirstOrDefault(x => x.Name == _ship.Text);
            if (selected is not null && selected.Name != "Custom") _capacity.Value = selected.CapacityScu;
            InvalidatePlan();
        };
        _capacity.ValueChanged += (_, _) => InvalidatePlan();
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
        _openHistory.Click += (_, _) => OpenHistoryFolder();
        _forgetImages.Click += (_, _) => ForgetProcessedImages();
        _clear.Click += (_, _) =>
        {
            _contracts.Clear(); _queued.Clear(); _currentManifest = null; _planWarning = null;
            _execution = null; _sessionStore.Clear();
            _lastPlannedFingerprint = null; _lastSavedPlanFingerprint = null; _displayedSelectionKey = "";
            _timerOverrideSelectionKey = "";
            _sendTablet.Enabled = false;
            _planRevision++;
            RefreshGrid();
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
                ? ManifestOptimizer.BuildSelected(selected, profile.CapacityScu, travelTimes: _travelTimes)
                : await Task.Run(() => ManifestOptimizer.Build(planningContracts, profile.CapacityScu,
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

    private ShipProfile CurrentShip() => new(_ship.Text.Length == 0 ? "ARGO RAFT" : _ship.Text, (int)_capacity.Value);

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
            ? ManifestOptimizer.BuildSelected(selected, profile.CapacityScu, travelTimes: _travelTimes)
            : _currentManifest is { Contracts.Count: > 0 }
                ? ManifestOptimizer.RefreshExisting(_currentManifest, profile.CapacityScu, travelTimes: _travelTimes)
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
        // Enabled whenever there is a plan, so clicking it can explain what is still missing for the tablet.
        _sendTablet.Enabled = manifest.Contracts.Count > 0;
        _planNow.Text = CheckedContracts().Count > 0 ? $"Plan {CheckedContracts().Count} checked job(s)" : "Plan suggested run";
        if (countdownTick) _grid.Invalidate();
        _ = PublishCurrentPlanAsync(manifest, profile);
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
            _ = PublishCurrentPlanAsync(_currentManifest, CurrentShip());
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
        var capacity = CurrentShip().CapacityScu;
        _comparePlans.Enabled = false;
        _status.Text = "Comparing route alternatives in the background…";
        List<PlanAlternative> alternatives;
        try
        {
            alternatives = await Task.Run(() => ManifestOptimizer.BuildAlternatives(contracts, capacity, DateTime.UtcNow, _travelTimes));
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
        _folder.Text = _settings.ScreenshotFolder;
        _ship.SelectedItem = _ships.Any(x => x.Name == _settings.ShipName) ? _settings.ShipName : "Custom";
        _capacity.Value = Math.Clamp(_settings.ShipCapacity, (int)_capacity.Minimum, (int)_capacity.Maximum);
        _apiBaseUrl = _settings.ApiBaseUrl;
    }

    private void SaveSettings()
    {
        _settings = new AppSettings
        {
            ScreenshotFolder = _folder.Text.Trim(),
            ShipName = _ship.Text, ShipCapacity = (int)_capacity.Value, PlanPreference = _planPreference,
            ApiBaseUrl = _apiBaseUrl
        };
        try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, JsonDefaults.Options())); } catch { }
    }

    private async Task ManageAccountAsync()
    {
        if (_authSession is not null)
        {
            var choice = MessageBox.Show($"Signed in as {_authSession.Account.DisplayName} ({_authSession.Account.Email}).\n\nSign out?",
                "Freight Planner account", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes) return;
            try { await new AccountClient(_apiBaseUrl).LogoutAsync(_authSession.RefreshToken, CancellationToken.None); }
            catch { /* Clear the local session even when the server is temporarily unavailable. */ }
            _authSession = null;
            _receiverCancellation?.Cancel();
            _receiverCancellation = null;
            _pairingClient = null;
            _pairingRegistration = null;
            _tabletLinked = false;
            _accountStatus.Text = "Signed out";
            _account.Text = "Account";
            _pairPhone.Text = "Pair phone";
            _pairPhone.Enabled = false;
            return;
        }

        using var dialog = new AccountForm(_apiBaseUrl);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Session is null) return;
        _apiBaseUrl = dialog.ApiBaseUrl;
        _authSession = dialog.Session;
        _account.Text = "Account";
        _accountStatus.Text = $"Signed in: {_authSession.Account.DisplayName}";
        _pairPhone.Enabled = true;
        SaveSettings();
    }

    private async Task PairPhoneAsync()
    {
        if (_authSession is null) return;
        _pairPhone.Enabled = false;
        try
        {
            var client = new AccountClient(_apiBaseUrl);
            if (_authSession.AccessTokenExpiresAtUtc <= DateTime.UtcNow.AddMinutes(1))
                _authSession = await client.RefreshAsync(_authSession.RefreshToken, CancellationToken.None);
            var registration = await client.CreatePairingAsync(_authSession.AccessToken, Environment.MachineName, CancellationToken.None);
            _receiverCancellation?.Cancel();
            _receiverCancellation = new CancellationTokenSource();
            _pairingClient = client;
            _pairingRegistration = registration;
            _tabletLinked = false;
            _lastPublishedMobilePlan = "";
            _sendTablet.Enabled = _currentManifest?.Contracts.Count > 0;
            _ = ReceivePhoneCommandsAsync(client, registration, _receiverCancellation.Token);
            _ = PublishCurrentPlanAsync(_currentManifest ?? new ManifestResult(), CurrentShip());
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

    private async Task ReceivePhoneCommandsAsync(AccountClient client, PairingRegistration registration, CancellationToken cancellationToken)
    {
        long lastCommandId = 0;
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
                    BeginInvoke(async () =>
                    {
                        _tabletLinked = true;
                        _pairPhone.Text = "Phone connected";
                        _pairingForm?.Close();
                        _sendTablet.Enabled = _currentManifest?.Contracts.Count > 0;
                        _accountStatus.Text = $"Tablet linked: {poll.DeviceName}";
                        if (_currentManifest is { Contracts.Count: > 0 })
                            await PublishCurrentPlanAsync(_currentManifest, CurrentShip(), force: true, announce: true);
                    });
                }
                foreach (var command in poll.Commands)
                {
                    lastCommandId = Math.Max(lastCommandId, command.Id);
                    if (command.Shortcut.StartsWith("FC1|", StringComparison.Ordinal))
                    {
                        if (!IsDisposed) BeginInvoke(() => ApplyTabletChecklistCommand(command));
                        continue;
                    }
                    if (command.CreatedAtUtc < DateTime.UtcNow.AddSeconds(-20)) continue;
                    if (!IsDisposed) BeginInvoke(() => _status.Text = $"Phone control ignored (game input disabled): {command.Label}");
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
        _pairingForm?.Close();
        _pairPhone.Text = "Pair phone";
        _pairPhone.Enabled = _authSession is not null;
        _accountStatus.Text = message;
    }

    private void ApplyTabletChecklistCommand(RemoteCommand command)
    {
        if (_currentManifest is not { Contracts.Count: > 0 }) return;
        var parts = command.Shortcut.Split('|');
        if (parts.Length != 6 || parts[0] != "FC1" ||
            !long.TryParse(parts[1], out _) || !int.TryParse(parts[2], out var stopIndex) ||
            !int.TryParse(parts[4], out var itemIndex) || !int.TryParse(parts[5], out var state))
        {
            _status.Text = "Tablet checklist update was malformed and was ignored";
            return;
        }

        var guide = ManifestOptimizer.BuildRouteGuide(_currentManifest.LocalRoute, _currentManifest, _travelTimes);
        if (stopIndex < 0 || stopIndex >= guide.Count) return;
        var kind = parts[3].ToUpperInvariant();
        if (kind is not ("D" or "P" or "K" or "S")) return;
        var done = state == 1;
        var fingerprint = ContractIdentity.PlanFingerprint(_currentManifest);
        if (_execution is null || !_execution.PlanFingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            _execution = new ExecutionProgress
            {
                PlanFingerprint = fingerprint,
                ShipName = CurrentShip().Name,
                Route = _currentManifest.LocalRoute.ToList()
            };
        }

        var progress = _execution;
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
                    var learnedLeg = _travelTimes.RecordObservation(guide[stopIndex - 1].Location, guide[stopIndex].Location, minutes, CurrentShip().Name);
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
                var handling = _travelTimes.RecordHandlingObservation(guide[stopIndex].Location, timestamp - started, CurrentShip().Name);
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
    }

    private static List<string> TabletChecklistKeys(int stopIndex, RouteGuideStep stop)
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
            await PublishCurrentPlanAsync(_currentManifest, CurrentShip(), force: true, announce: true);
        }
        finally
        {
            _sendTablet.Text = "Send to tablet";
            _sendTablet.Enabled = _currentManifest.Contracts.Count > 0;
        }
    }

    private async Task<bool> PublishCurrentPlanAsync(ManifestResult manifest, ShipProfile ship, bool force = false, bool announce = false)
    {
        var client = _pairingClient;
        var registration = _pairingRegistration;
        if (client is null || registration is null) return false;

        var title = manifest.Contracts.Count == 0
            ? "NO ACTIVE FLIGHT PLAN"
            : manifest.TimerBudgetOverridden ? "TIMER OVERRIDE FLIGHT PLAN"
            : manifest.HasUnverifiedValues ? "UNVERIFIED FLIGHT PLAN"
            : "CURRENT FLIGHT PLAN";
        var summary = manifest.Contracts.Count == 0
            ? "Scan contracts on the PC to publish a route."
            : $"{ship.Name} • {manifest.TotalScu:N0}/{ship.CapacityScu:N0} SCU • " +
              (manifest.UnknownRewardCount > 0 ? $"{manifest.TotalReward:N0} known aUEC" : $"{manifest.TotalReward:N0} aUEC") +
              $" • ~{manifest.EstimatedTripMinutes:N0} min";
        var stops = ManifestOptimizer.BuildRouteGuide(manifest.LocalRoute, manifest, _travelTimes)
            .Select(x => new MobilePlanStop(x.Number, x.Location, x.TravelMinutesFromPrevious,
                x.Deliveries, x.Pickups, x.Packing)).ToArray();
        var activeExecution = manifest.Contracts.Count > 0 &&
                              _execution?.PlanFingerprint.Equals(ContractIdentity.PlanFingerprint(manifest), StringComparison.OrdinalIgnoreCase) == true
            ? _execution
            : null;
        var currentStopIndex = manifest.Contracts.Count == 0 ? -1 : Math.Clamp(activeExecution?.CurrentStopIndex ?? 0, 0, Math.Max(0, stops.Length - 1));
        var currentStopCompleted = activeExecution?.CurrentStopCompleted ?? false;
        var contentFingerprint = JsonSerializer.Serialize(new { title, summary, currentStopIndex, currentStopCompleted, stops }, JsonDefaults.Options());

        await _mobilePlanPublishGate.WaitAsync();
        try
        {
            if (!force && contentFingerprint == _lastPublishedMobilePlan) return true;
            var snapshot = new MobilePlanSnapshot(++_mobilePlanRevision, DateTime.UtcNow, title, summary,
                currentStopIndex, currentStopCompleted, stops);
            await client.PublishPlanAsync(registration.SessionId, registration.ReceiverToken, snapshot, CancellationToken.None);
            _lastPublishedMobilePlan = contentFingerprint;
            if (announce && !IsDisposed)
            {
                _accountStatus.Text = $"Tablet synced: {stops.Length} stops at {DateTime.Now:HH:mm:ss}";
                _status.Text = $"Flight plan sent to tablet — {manifest.Contracts.Count} job(s), {stops.Length} route stop(s)";
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
        if (_contracts.Count > 0)
        {
            RefreshGrid();
            _status.Text = $"Restored {_contracts.Count:N0} contract(s) from the active session";
        }
    }

    private void SaveSession()
    {
        try { _sessionStore.Save(_contracts, _execution); } catch { }
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
