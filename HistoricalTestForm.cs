using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace FreightCopilot;

public sealed class HistoricalTestForm : Form
{
    private readonly List<HistoricalContractRow> _rows;
    private readonly DateTime _planningUtc;
    private readonly TravelTimeDatabase _travelTimes;
    private readonly ShipProfile _ship;
    private readonly int _sourceScreenshotCount;
    private readonly string _reportPath;
    private ManifestResult _current = new();
    private bool _updating;
    private string? _sortColumn;
    private bool _sortAscending = true;

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AutoGenerateColumns = false,
        AllowUserToAddRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly TextBox _report = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Segoe UI", 10)
    };
    private readonly Label _summary = new() { AutoSize = true, ForeColor = Color.FromArgb(188, 204, 220) };
    private readonly Button _openRoute = new() { Text = "Open route test", AutoSize = true };
    private readonly Button _openMap = new() { Text = "Open travel map", AutoSize = true };

    public HistoricalTestForm(IReadOnlyList<ContractScan> scans, TravelTimeDatabase travelTimes, ShipProfile ship, string appDirectory, int? sourceScreenshotCount = null)
    {
        _planningUtc = scans.Count > 0 ? scans.Max(x => x.CapturedAtUtc) : DateTime.UtcNow;
        _travelTimes = travelTimes;
        _ship = ship;
        _sourceScreenshotCount = sourceScreenshotCount ?? scans.Count;
        _rows = scans.Select(x => new HistoricalContractRow(x, _planningUtc)).ToList();
        var reports = Path.Combine(appDirectory, "test-reports");
        Directory.CreateDirectory(reports);
        _reportPath = Path.Combine(reports, $"historical-ocr-route-test-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        Text = "Freight Planner — Historical OCR & Route Test";
        Width = 1280;
        Height = 860;
        MinimumSize = new Size(920, 650);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Color.FromArgb(20, 31, 46) };
        header.Controls.Add(new Label
        {
            Text = "HISTORICAL OCR & ROUTE TEST",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 18),
            AutoSize = true,
            Location = new Point(20, 12)
        });
        _summary.Location = new Point(22, 49);
        header.Controls.Add(_summary);

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Plan", DataPropertyName = nameof(HistoricalContractRow.Include), Width = 48 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "OCR", DataPropertyName = nameof(HistoricalContractRow.Quality), Width = 80, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TimeLeft", HeaderText = "Time at test", DataPropertyName = nameof(HistoricalContractRow.TimeAtTest), Width = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Reward", HeaderText = "Reward", DataPropertyName = nameof(HistoricalContractRow.Reward), Width = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0" } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SCU", DataPropertyName = nameof(HistoricalContractRow.TotalScu), Width = 55, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Title", DataPropertyName = nameof(HistoricalContractRow.Title), Width = 235, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Pickup", HeaderText = "Pickup", DataPropertyName = nameof(HistoricalContractRow.Pickups), Width = 220, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Destination", HeaderText = "Destination", DataPropertyName = nameof(HistoricalContractRow.Destinations), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.DataSource = new BindingList<HistoricalContractRow>(_rows);
        _grid.ColumnHeaderMouseClick += (_, e) => SortRowsBy(_grid.Columns[e.ColumnIndex].Name);
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_updating || e.RowIndex < 0 || e.ColumnIndex != _grid.Columns["Include"].Index) return;
            if (_grid.Rows[e.RowIndex].DataBoundItem is not HistoricalContractRow row) return;
            if (row.Include && !row.Scan.IsEligible)
            {
                row.Include = false;
                _grid.InvalidateRow(e.RowIndex);
                MessageBox.Show(row.Scan.Notes, "OCR row cannot be routed automatically", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Rebuild(useCheckedSelection: true);
        };
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            if (_grid.Rows[e.RowIndex].DataBoundItem is not HistoricalContractRow row) return;
            _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = row.Scan.IsEligible ? Color.Honeydew : Color.MistyRose;
            if (e.ColumnIndex == _grid.Columns.Cast<DataGridViewColumn>().First(x => x.DataPropertyName == nameof(HistoricalContractRow.Reward)).Index && row.Reward <= 0)
            {
                e.Value = "Unknown";
                e.FormattingApplied = true;
            }
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 385 };
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(_report);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = Color.White };
        var close = new Button { Text = "Close", AutoSize = true };
        var auto = new Button { Text = "Rebuild automatic test", AutoSize = true };
        var openReports = new Button { Text = "Open test reports", AutoSize = true };
        close.Click += (_, _) => Close();
        auto.Click += (_, _) => Rebuild(useCheckedSelection: false);
        _openRoute.Click += (_, _) => OpenRoute();
        _openMap.Click += (_, _) => OpenMap();
        openReports.Click += (_, _) => Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_reportPath)!) { UseShellExecute = true });
        footer.Controls.AddRange([close, _openRoute, _openMap, auto, openReports]);

        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(header);
        Rebuild(useCheckedSelection: false);
    }

    private void SortRowsBy(string columnName)
    {
        if (columnName is not ("Pickup" or "Destination" or "TimeLeft" or "Reward")) return;
        if (_sortColumn == columnName) _sortAscending = !_sortAscending;
        else { _sortColumn = columnName; _sortAscending = true; }

        var comparer = StringComparer.CurrentCultureIgnoreCase;
        IEnumerable<HistoricalContractRow> sorted = columnName switch
        {
            "Pickup" => _sortAscending ? _rows.OrderBy(x => x.Pickups, comparer) : _rows.OrderByDescending(x => x.Pickups, comparer),
            "Destination" => _sortAscending ? _rows.OrderBy(x => x.Destinations, comparer) : _rows.OrderByDescending(x => x.Destinations, comparer),
            "TimeLeft" => _sortAscending ? _rows.OrderBy(x => x.SortableTimeLeft) : _rows.OrderByDescending(x => x.SortableTimeLeft),
            "Reward" => _sortAscending ? _rows.OrderBy(x => x.Reward) : _rows.OrderByDescending(x => x.Reward),
            _ => _rows
        };
        _grid.DataSource = new BindingList<HistoricalContractRow>(sorted.ToList());
        foreach (DataGridViewColumn column in _grid.Columns)
            column.HeaderCell.SortGlyphDirection = column.Name == _sortColumn
                ? (_sortAscending ? SortOrder.Ascending : SortOrder.Descending)
                : SortOrder.None;
    }

    private void Rebuild(bool useCheckedSelection)
    {
        var selected = useCheckedSelection ? _rows.Where(x => x.Include).Select(x => x.Scan).ToList() : [];
        _current = selected.Count > 0
            ? ManifestOptimizer.BuildSelected(selected, _ship, _planningUtc, _travelTimes)
            : ManifestOptimizer.Build(_rows.Select(x => x.Scan).ToList(), _ship, _planningUtc, _travelTimes);

        if (!useCheckedSelection)
        {
            _updating = true;
            foreach (var row in _rows) row.Include = _current.Contracts.Contains(row.Scan);
            _grid.Refresh();
            _updating = false;
        }

        var warnings = _rows.Count(x => !x.Scan.IsEligible);
        var recordedLegs = _current.TravelLegs.Count(x => x.IsRecorded);
        _summary.Text = $"{_sourceScreenshotCount:N0} screenshot(s) • {_rows.Count:N0} unique mission(s) • {_rows.Count - warnings:N0} OCR-valid • {warnings:N0} warning(s) • " +
                        $"test clock {TimeZoneInfo.ConvertTimeFromUtc(_planningUtc, TimeZoneInfo.Local):g} • {_current.Contracts.Count:N0} planned contract(s) • " +
                        $"{recordedLegs}/{_current.TravelLegs.Count} recorded travel legs";
        _report.Text = ManifestOptimizer.Format(_current, _ship, _rows.Select(x => x.Scan).ToList());
        _openRoute.Enabled = _current.Contracts.Count > 0;
        _openMap.Enabled = _current.Contracts.Count > 0;
        SaveReport();
    }

    private void OpenRoute()
    {
        if (_current.Contracts.Count == 0) return;
        new RoutePlanForm(_current, _ship, _report.Text, _travelTimes).Show(this);
    }

    private void OpenMap()
    {
        if (_current.Contracts.Count == 0) return;
        var map = new TravelMapForm(_travelTimes, _current);
        map.FormClosed += (_, _) => Rebuild(useCheckedSelection: true);
        map.Show(this);
    }

    private void SaveReport()
    {
        try
        {
            var snapshot = new
            {
                schema_version = 1,
                created_local = DateTimeOffset.Now,
                historical_planning_utc = _planningUtc,
                ship = _ship,
                summary = new
                {
                    screenshots = _sourceScreenshotCount,
                    unique_missions = _rows.Count,
                    ocr_valid = _rows.Count(x => x.Scan.IsEligible),
                    ocr_warnings = _rows.Count(x => !x.Scan.IsEligible),
                    selected_contracts = _current.Contracts.Count,
                    cargo_scu = _current.TotalScu,
                    known_reward_auec = _current.TotalReward,
                    estimated_trip_minutes = _current.EstimatedTripMinutes,
                    estimated_flight_minutes = _current.EstimatedFlightMinutes,
                    timer_headroom_minutes = _current.HeadroomMinutes
                },
                route = _current.LocalRoute,
                travel_legs = _current.TravelLegs,
                records = _rows.Select(row => new
                {
                    source_image = row.Scan.SourceFile,
                    row.Scan.Title,
                    reward_auec = row.Scan.Reward > 0 ? row.Scan.Reward : (int?)null,
                    total_scu = row.Scan.TotalScu,
                    availability_minutes_at_capture = row.Scan.AvailabilityMinutes,
                    contract_deadline_not_applicable = row.Scan.ContractDeadlineNotApplicable,
                    remaining_minutes_at_test = row.Scan.AvailabilityMinutes is > 0 ? row.Scan.RemainingMinutes(_planningUtc) : (int?)null,
                    eligible = row.Scan.IsEligible,
                    row.Scan.Warnings,
                    row.Scan.Cargo
                }),
                formatted_manifest = _report.Text
            };
            File.WriteAllText(_reportPath, JsonSerializer.Serialize(snapshot, JsonDefaults.Options()));
        }
        catch { }
    }

    private sealed class HistoricalContractRow(ContractScan scan, DateTime planningUtc)
    {
        public ContractScan Scan { get; } = scan;
        public bool Include { get; set; }
        public string Quality => Scan.IsEligible ? "Validated" : "Warning";
        public string TimeAtTest
        {
            get
            {
                if (Scan.ContractDeadlineNotApplicable) return "Accepted";
                if (Scan.AvailabilityMinutes is not > 0) return "Unknown";
                var minutes = Scan.RemainingMinutes(planningUtc);
                return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:D2}m" : $"{minutes}m";
            }
        }
        public int Reward => Scan.Reward;
        public int SortableTimeLeft => Scan.ContractDeadlineNotApplicable ? int.MaxValue :
            Scan.AvailabilityMinutes is > 0 ? Scan.RemainingMinutes(planningUtc) : int.MaxValue - 1;
        public int TotalScu => Scan.TotalScu;
        public string Title => Scan.Title;
        public string Pickups => Scan.Pickups;
        public string Destinations => Scan.Destinations;
    }
}
