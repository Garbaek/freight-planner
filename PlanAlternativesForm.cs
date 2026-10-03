namespace FreightCopilot;

public sealed class PlanAlternativesForm : Form
{
    private readonly IReadOnlyList<PlanAlternative> _alternatives;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AutoGenerateColumns = false,
        AllowUserToAddRows = false,
        ReadOnly = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false
    };

    public PlanPreference? SelectedPreference { get; private set; }

    public PlanAlternativesForm(IReadOnlyList<PlanAlternative> alternatives, PlanPreference current)
    {
        _alternatives = alternatives;
        Text = "Freight Planner — Compare Local Plans";
        Width = 1120;
        Height = 480;
        MinimumSize = new Size(850, 380);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.FromArgb(20, 31, 46) };
        header.Controls.Add(new Label { Text = "COMPARE THREE LOCAL ROUTE PLANS", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 18), AutoSize = true, Location = new Point(20, 12) });
        header.Controls.Add(new Label { Text = "All choices enforce cargo capacity, pickup-before-delivery, timers, and the safety reserve.", ForeColor = Color.FromArgb(188, 204, 220), AutoSize = true, Location = new Point(22, 46) });

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Goal", DataPropertyName = nameof(AlternativeRow.Goal), Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Reward", DataPropertyName = nameof(AlternativeRow.Reward), Width = 90, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0" } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SCU", DataPropertyName = nameof(AlternativeRow.Scu), Width = 65 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Contracts", DataPropertyName = nameof(AlternativeRow.Contracts), Width = 78 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Run", DataPropertyName = nameof(AlternativeRow.Run), Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Headroom", DataPropertyName = nameof(AlternativeRow.Headroom), Width = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Route", DataPropertyName = nameof(AlternativeRow.Route), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.DataSource = alternatives.Select(x => new AlternativeRow(x)).ToList();

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = Color.White };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        var use = new Button { Text = "Use selected plan", AutoSize = true };
        cancel.Click += (_, _) => Close();
        use.Click += (_, _) => Choose();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Choose(); };
        footer.Controls.AddRange([cancel, use]);
        Controls.Add(_grid);
        Controls.Add(footer);
        Controls.Add(header);

        var index = alternatives.ToList().FindIndex(x => x.Preference == current);
        if (_grid.Rows.Count > 0) _grid.Rows[Math.Max(0, index)].Selected = true;
    }

    private void Choose()
    {
        if (_grid.CurrentRow?.DataBoundItem is not AlternativeRow row) return;
        SelectedPreference = row.Alternative.Preference;
        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed class AlternativeRow
    {
        public AlternativeRow(PlanAlternative alternative) => Alternative = alternative;
        public PlanAlternative Alternative { get; }
        public string Goal => Alternative.Name;
        public int Reward => Alternative.Manifest.TotalReward;
        public int Scu => Alternative.Manifest.TotalScu;
        public int Contracts => Alternative.Manifest.Contracts.Count;
        public string Run => $"{Alternative.Manifest.EstimatedTripMinutes}m";
        public string Headroom => Alternative.Manifest.HasKnownAvailability ? $"{Alternative.Manifest.HeadroomMinutes}m" : "Accepted";
        public string Route => string.Join(" → ", Alternative.Manifest.LocalRoute.Select(ManifestOptimizer.MapLocationLabel));
    }
}
