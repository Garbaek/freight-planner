using System.ComponentModel;

namespace FreightCopilot;

public sealed class ExecutionForm : Form
{
    private readonly ManifestResult _manifest;
    private readonly ShipProfile _ship;
    private readonly TravelTimeDatabase _travelTimes;
    private readonly ExecutionProgress _progress;
    private readonly Action<ExecutionProgress> _save;
    private readonly BindingList<ExecutionTaskRow> _tasks;
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly Label _current = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 15), ForeColor = Color.White };
    private readonly Label _summary = new() { AutoSize = true, ForeColor = Color.FromArgb(188, 204, 220) };
    private readonly Label _timer = new() { AutoSize = true, ForeColor = Color.FromArgb(255, 206, 112) };
    private readonly Button _completeStop = new() { Text = "Complete current stop", AutoSize = true };
    private readonly Button _depart = new() { Text = "Start next leg", AutoSize = true };
    private readonly Button _arrive = new() { Text = "Arrived — record time", AutoSize = true };
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };

    public ExecutionForm(ManifestResult manifest, ShipProfile ship, TravelTimeDatabase travelTimes,
        ExecutionProgress progress, Action<ExecutionProgress> save)
    {
        _manifest = manifest;
        _ship = ship;
        _travelTimes = travelTimes;
        _progress = progress;
        _save = save;
        _tasks = new BindingList<ExecutionTaskRow>(BuildTasks(manifest, progress));

        Text = "Freight Planner — Flight Execution";
        Width = 1240;
        Height = 780;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 100, BackColor = Color.FromArgb(20, 31, 46) };
        _current.Location = new Point(20, 12);
        _summary.Location = new Point(22, 48);
        _timer.Location = new Point(22, 72);
        header.Controls.AddRange([_current, _summary, _timer]);

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Done", HeaderText = "Done", DataPropertyName = nameof(ExecutionTaskRow.Done), Width = 52 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Stop", DataPropertyName = nameof(ExecutionTaskRow.Stop), Width = 55, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Action", DataPropertyName = nameof(ExecutionTaskRow.Action), Width = 85, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Details", DataPropertyName = nameof(ExecutionTaskRow.Details), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Required", DataPropertyName = nameof(ExecutionTaskRow.RequiredDisplay), Width = 90, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Loaded", HeaderText = "Loaded SCU", DataPropertyName = nameof(ExecutionTaskRow.LoadedScu), Width = 92 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = nameof(ExecutionTaskRow.Status), Width = 120, ReadOnly = true });
        _grid.DataSource = _tasks;
        _grid.DataError += (_, _) => { };
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) SaveRow(_grid.Rows[e.RowIndex].DataBoundItem as ExecutionTaskRow); };
        _grid.CellFormatting += (_, e) => FormatRow(e.RowIndex);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = Color.White };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        _completeStop.Click += (_, _) => CompleteCurrentStop();
        _depart.Click += (_, _) => StartLeg();
        _arrive.Click += (_, _) => Arrive();
        footer.Controls.AddRange([close, _arrive, _depart, _completeStop]);

        Controls.Add(_grid);
        Controls.Add(footer);
        Controls.Add(header);
        _clock.Tick += (_, _) => UpdateHeader();
        _clock.Start();
        FormClosed += (_, _) => { _clock.Stop(); Persist(); };
        UpdateHeader();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock.Stop();
            _clock.Dispose();
        }
        base.Dispose(disposing);
    }

    private void SaveRow(ExecutionTaskRow? row)
    {
        if (row is null) return;
        if (row.Action == "Load")
        {
            row.LoadedScu = Math.Clamp(row.LoadedScu, 0, row.RequiredScu);
            _progress.LoadedScu[row.Key] = row.LoadedScu;
            if (row.LoadedScu != row.RequiredScu) row.Done = false;
        }
        _progress.CompletedTasks[row.Key] = row.Done;
        Persist();
        _grid.Refresh();
        UpdateHeader();
    }

    private void CompleteCurrentStop()
    {
        _grid.EndEdit();
        if (_progress.CurrentStopIndex >= _manifest.LocalRoute.Count) return;
        var acceptanceMissing = _tasks.Where(x => x.Action == "Accept" && !x.Done).ToList();
        if (_progress.CurrentStopIndex == 0 && acceptanceMissing.Count > 0)
        {
            MessageBox.Show("Check every contract as accepted before completing the first stop.", "Contracts not confirmed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var shortages = _tasks.Where(x => x.StopIndex == _progress.CurrentStopIndex && x.Action == "Load" && x.LoadedScu != x.RequiredScu).ToList();
        if (shortages.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, shortages.Select(x => $"{x.Details}: {x.LoadedScu}/{x.RequiredScu} SCU loaded")),
                "Cargo discrepancy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        foreach (var task in _tasks.Where(x => x.StopIndex == _progress.CurrentStopIndex))
        {
            task.Done = true;
            _progress.CompletedTasks[task.Key] = true;
        }
        _progress.CurrentStopCompleted = true;
        Persist();
        _grid.Refresh();
        UpdateHeader();
        if (_progress.CurrentStopIndex == _manifest.LocalRoute.Count - 1)
            MessageBox.Show("All route stops and cargo actions are complete.", "Freight run complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void StartLeg()
    {
        if (!_progress.CurrentStopCompleted)
        {
            MessageBox.Show("Complete the current stop first.", "Current stop unfinished", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_progress.CurrentStopIndex >= _manifest.LocalRoute.Count - 1) return;
        _progress.TimingFrom = _manifest.LocalRoute[_progress.CurrentStopIndex];
        _progress.TimingTo = _manifest.LocalRoute[_progress.CurrentStopIndex + 1];
        _progress.LegStartedAtUtc = DateTime.UtcNow;
        Persist();
        UpdateHeader();
    }

    private void Arrive()
    {
        if (_progress.LegStartedAtUtc is null) return;
        var elapsed = DateTime.UtcNow - _progress.LegStartedAtUtc.Value;
        var minutes = Math.Max(1, (int)Math.Round(elapsed.TotalMinutes));
        var learned = _travelTimes.RecordObservation(_progress.TimingFrom, _progress.TimingTo, minutes, _ship.Name);
        _progress.CurrentStopIndex = Math.Min(_progress.CurrentStopIndex + 1, _manifest.LocalRoute.Count - 1);
        _progress.CurrentStopCompleted = false;
        _progress.LegStartedAtUtc = null;
        _progress.TimingFrom = "";
        _progress.TimingTo = "";
        Persist();
        _grid.Refresh();
        UpdateHeader();
        MessageBox.Show($"Recorded {minutes} minute(s). Learned average: {learned.Minutes} minutes from {learned.SampleCount} run(s) for {_ship.Name}.",
            "Travel time learned", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void UpdateHeader()
    {
        var route = _manifest.LocalRoute;
        if (route.Count == 0) return;
        var index = Math.Clamp(_progress.CurrentStopIndex, 0, route.Count - 1);
        _current.Text = $"STOP {index + 1}/{route.Count} — {ManifestOptimizer.MapLocationLabel(route[index])}";
        var loadTasks = _tasks.Where(x => x.Action == "Load").ToList();
        var shortage = loadTasks.Sum(x => Math.Max(0, x.RequiredScu - x.LoadedScu));
        var onboard = loadTasks.Where(x => x.Done).Sum(x => x.LoadedScu) - _tasks.Where(x => x.Action == "Deliver" && x.Done).Sum(x => x.RequiredScu);
        _summary.Text = $"On board: {Math.Max(0, onboard):N0} SCU  •  Not yet loaded: {shortage:N0} SCU  •  {_tasks.Count(x => x.Done)}/{_tasks.Count} checklist items complete";
        _summary.ForeColor = shortage > 0 ? Color.FromArgb(255, 206, 112) : Color.FromArgb(124, 211, 177);
        if (_progress.LegStartedAtUtc is not null)
        {
            var elapsed = DateTime.UtcNow - _progress.LegStartedAtUtc.Value;
            _timer.Text = $"TIMING: {ManifestOptimizer.MapLocationLabel(_progress.TimingFrom)} → {ManifestOptimizer.MapLocationLabel(_progress.TimingTo)} — {elapsed.ToString(@"hh\:mm\:ss")}";
        }
        else _timer.Text = _progress.CurrentStopCompleted && index < route.Count - 1 ? "Stop complete — ready to start the next leg" : "";
        _completeStop.Enabled = !_progress.CurrentStopCompleted;
        _depart.Enabled = _progress.CurrentStopCompleted && index < route.Count - 1 && _progress.LegStartedAtUtc is null;
        _arrive.Enabled = _progress.LegStartedAtUtc is not null;
    }

    private void FormatRow(int rowIndex)
    {
        if (rowIndex < 0 || _grid.Rows[rowIndex].DataBoundItem is not ExecutionTaskRow row) return;
        _grid.Rows[rowIndex].DefaultCellStyle.BackColor = row.Done ? Color.Honeydew :
            row.StopIndex == _progress.CurrentStopIndex ? Color.LemonChiffon : Color.White;
        _grid.Rows[rowIndex].Cells["Loaded"].ReadOnly = row.Action != "Load";
        if (row.Action == "Load" && row.LoadedScu != row.RequiredScu)
            _grid.Rows[rowIndex].Cells["Loaded"].Style.BackColor = Color.MistyRose;
    }

    private void Persist()
    {
        foreach (var row in _tasks)
        {
            _progress.CompletedTasks[row.Key] = row.Done;
            if (row.Action == "Load") _progress.LoadedScu[row.Key] = row.LoadedScu;
        }
        _save(_progress);
    }

    private static List<ExecutionTaskRow> BuildTasks(ManifestResult manifest, ExecutionProgress progress)
    {
        var rows = new List<ExecutionTaskRow>();
        foreach (var contract in manifest.Contracts.OrderByDescending(x => x.Reward))
        {
            var fp = ContractIdentity.Fingerprint(contract);
            rows.Add(Make($"accept|{fp}", -1, "Accept", $"{contract.Reward:N0} aUEC — {contract.Title}", 0));
            for (var cargoIndex = 0; cargoIndex < contract.Cargo.Count; cargoIndex++)
            {
                var leg = contract.Cargo[cargoIndex];
                var pickupIndex = FindStop(manifest.LocalRoute, leg.Pickup, 0);
                var deliveryIndex = FindStop(manifest.LocalRoute, leg.Destination, Math.Max(0, pickupIndex + 1));
                var identity = $"{fp}|{cargoIndex}";
                rows.Add(Make($"collect|{identity}", pickupIndex, "Collect", $"{leg.Scu} SCU {leg.Commodity} for {ManifestOptimizer.MapLocationLabel(leg.Destination)}", leg.Scu));
                rows.Add(Make($"load|{identity}", pickupIndex, "Load", $"{leg.Commodity} — crates {CratePlanner.Describe(leg.Scu, contract.MaxContainerScu)}", leg.Scu));
                rows.Add(Make($"deliver|{identity}", deliveryIndex, "Deliver", $"{leg.Scu} SCU {leg.Commodity}", leg.Scu));
            }
        }
        foreach (var row in rows)
        {
            row.Done = progress.CompletedTasks.GetValueOrDefault(row.Key);
            if (row.Action == "Load") row.LoadedScu = progress.LoadedScu.GetValueOrDefault(row.Key);
        }
        return rows.OrderBy(x => x.StopIndex).ThenBy(x => ActionOrder(x.Action)).ToList();

        ExecutionTaskRow Make(string key, int stop, string action, string details, int required) => new()
        {
            Key = key, StopIndex = stop, Stop = stop < 0 ? "—" : (stop + 1).ToString(), Action = action, Details = details, RequiredScu = required
        };
    }

    private static int FindStop(IReadOnlyList<string> route, string location, int start)
    {
        var target = ManifestOptimizer.NormalizeLocation(location.Split(" + ", StringSplitOptions.TrimEntries)[0]);
        for (var i = Math.Max(0, start); i < route.Count; i++)
            if (ManifestOptimizer.NormalizeLocation(route[i]) == target) return i;
        return Math.Clamp(start, 0, Math.Max(0, route.Count - 1));
    }

    private static int ActionOrder(string action) => action switch { "Accept" => 0, "Deliver" => 1, "Collect" => 2, "Load" => 3, _ => 4 };

    private sealed class ExecutionTaskRow
    {
        public string Key { get; set; } = "";
        public int StopIndex { get; set; }
        public string Stop { get; set; } = "";
        public string Action { get; set; } = "";
        public string Details { get; set; } = "";
        public int RequiredScu { get; set; }
        public string RequiredDisplay => RequiredScu > 0 ? $"{RequiredScu} SCU" : "";
        public int LoadedScu { get; set; }
        public bool Done { get; set; }
        public string Status => Action == "Load" ? LoadedScu == RequiredScu ? "Ready" : $"{LoadedScu}/{RequiredScu}" : Done ? "Done" : "Pending";
    }
}
