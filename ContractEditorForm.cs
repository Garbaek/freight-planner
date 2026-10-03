using System.ComponentModel;

namespace FreightCopilot;

public sealed class ContractEditorForm : Form
{
    private readonly ContractScan _scan;
    private readonly TextBox _title = new() { Dock = DockStyle.Fill };
    private readonly TextBox _contractor = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _reward = new() { Dock = DockStyle.Fill, Maximum = 10_000_000, ThousandsSeparator = true };
    private readonly NumericUpDown _timer = new() { Dock = DockStyle.Fill, Maximum = 24 * 60 };
    private readonly NumericUpDown _container = new() { Dock = DockStyle.Fill, Maximum = 128 };
    private readonly CheckBox _accepted = new() { Text = "Already accepted — Contract Deadline N/A", AutoSize = true };
    private readonly BindingList<CargoLeg> _cargo;
    private readonly DataGridView _cargoGrid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = true, AllowUserToDeleteRows = true };
    private readonly PictureBox _image = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(28, 34, 41) };
    private readonly TextBox _raw = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9), WordWrap = false };
    private readonly Label _warnings = new() { Dock = DockStyle.Fill, AutoSize = false, ForeColor = Color.FromArgb(150, 45, 35), Padding = new Padding(6) };

    public ContractEditorForm(ContractScan scan)
    {
        _scan = scan;
        _cargo = new BindingList<CargoLeg>(scan.Cargo.Select(x => new CargoLeg
        {
            Commodity = x.Commodity ?? "", Scu = x.Scu, Pickup = x.Pickup ?? "", Destination = x.Destination ?? ""
        }).ToList());
        Text = "Freight Planner — Correct Contract & Inspect OCR";
        Width = 1280;
        Height = 830;
        MinimumSize = new Size(900, 650);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Color.FromArgb(20, 31, 46) };
        header.Controls.Add(new Label { Text = "CONTRACT CORRECTION & OCR INSPECTION", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 18), AutoSize = true, Location = new Point(20, 11) });
        header.Controls.Add(new Label { Text = Path.GetFileName(scan.SourceFile), ForeColor = Color.FromArgb(188, 204, 220), AutoSize = true, Location = new Point(22, 44) });

        var fields = new TableLayoutPanel { Dock = DockStyle.Top, Height = 145, ColumnCount = 4, Padding = new Padding(8) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        AddField(fields, 0, "Title", _title, "Reward aUEC", _reward);
        AddField(fields, 1, "Contractor", _contractor, "Time at capture", _timer);
        AddField(fields, 2, "Accepted", _accepted, "Max crate SCU", _container);
        fields.RowCount = 3;

        _cargoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Commodity", DataPropertyName = nameof(CargoLeg.Commodity), Width = 180 });
        _cargoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SCU", DataPropertyName = nameof(CargoLeg.Scu), Width = 70 });
        _cargoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Pickup", DataPropertyName = nameof(CargoLeg.Pickup), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 45 });
        _cargoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Destination", DataPropertyName = nameof(CargoLeg.Destination), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 55 });
        _cargoGrid.DataSource = _cargo;
        _cargoGrid.DataError += (_, _) => { };

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 145));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        right.Controls.Add(fields, 0, 0);
        right.Controls.Add(_cargoGrid, 0, 1);
        right.Controls.Add(_warnings, 0, 2);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var imageTab = new TabPage("Screenshot") { Padding = new Padding(4) };
        var rawTab = new TabPage("Raw OCR") { Padding = new Padding(4) };
        imageTab.Controls.Add(_image);
        rawTab.Controls.Add(_raw);
        tabs.TabPages.AddRange([imageTab, rawTab]);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 500 };
        split.Panel1.Controls.Add(tabs);
        split.Panel2.Controls.Add(right);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = Color.White };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        var save = new Button { Text = "Save correction", AutoSize = true };
        var open = new Button { Text = "Open original image", AutoSize = true };
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => TrySaveCorrection();
        open.Click += (_, _) => { if (File.Exists(_scan.SourceFile)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_scan.SourceFile) { UseShellExecute = true }); };
        footer.Controls.AddRange([cancel, save, open]);

        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(header);
        LoadValues();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image.Image?.Dispose();
        base.Dispose(disposing);
    }

    private void LoadValues()
    {
        _title.Text = _scan.Title;
        _contractor.Text = _scan.Contractor;
        _reward.Value = Math.Clamp(_scan.Reward, 0, (int)_reward.Maximum);
        _timer.Value = Math.Clamp(_scan.AvailabilityMinutes ?? 0, 0, (int)_timer.Maximum);
        _container.Value = Math.Clamp(_scan.MaxContainerScu ?? 0, 0, (int)_container.Maximum);
        _accepted.Checked = _scan.ContractDeadlineNotApplicable;
        _timer.Enabled = !_accepted.Checked;
        _accepted.CheckedChanged += (_, _) => _timer.Enabled = !_accepted.Checked;
        _raw.Text = _scan.RawOcrText;
        _warnings.Text = _scan.Warnings.Count == 0
            ? "✓ OCR currently validates. You can still correct any visible discrepancy."
            : "OCR warnings:" + Environment.NewLine + string.Join(Environment.NewLine, _scan.Warnings.Select(x => $"• {x}"));
        _warnings.ForeColor = _scan.Warnings.Count == 0 ? Color.FromArgb(23, 128, 93) : Color.FromArgb(150, 45, 35);
        if (_scan.Reward <= 0) _reward.BackColor = Color.MistyRose;
        if (_scan.AvailabilityMinutes is not > 0 && !_scan.ContractDeadlineNotApplicable) _timer.BackColor = Color.MistyRose;
        if (_scan.Title.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) _title.BackColor = Color.MistyRose;
        LoadImage();
    }

    private void LoadImage()
    {
        try
        {
            if (!File.Exists(_scan.SourceFile)) return;
            using var stream = new FileStream(_scan.SourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var source = Image.FromStream(stream);
            _image.Image = new Bitmap(source);
        }
        catch { }
    }

    private void SaveCorrection()
    {
        _cargoGrid.EndEdit();
        var cargo = _cargo.Where(x => x.Scu > 0 || !string.IsNullOrWhiteSpace(x.Commodity) || !string.IsNullOrWhiteSpace(x.Pickup) || !string.IsNullOrWhiteSpace(x.Destination)).ToList();
        if (cargo.Count == 0)
        {
            MessageBox.Show("Add at least one cargo row.", "Contract correction", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _scan.Title = _title.Text.Trim();
        _scan.Contractor = _contractor.Text.Trim();
        _scan.Reward = (int)_reward.Value;
        _scan.ContractDeadlineNotApplicable = _accepted.Checked;
        _scan.AvailabilityMinutes = _accepted.Checked || _timer.Value <= 0 ? null : (int)_timer.Value;
        _scan.MaxContainerScu = _container.Value <= 0 ? null : (int)_container.Value;
        _scan.Cargo = cargo;
        _scan.ManuallyCorrected = true;
        _scan.ManualOverride = false;
        if (!_scan.RecoveredFields.Contains("Values manually corrected after OCR inspection."))
            _scan.RecoveredFields.Add("Values manually corrected after OCR inspection.");
        MissionParser.Revalidate(_scan);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void TrySaveCorrection()
    {
        try
        {
            SaveCorrection();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The correction could not be saved. Your entries are still in this window.\n\n{ex.Message}",
                "Contract correction",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void AddField(TableLayoutPanel panel, int row, string label1, Control control1, string label2, Control control2)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 41));
        panel.Controls.Add(new Label { Text = label1, Anchor = AnchorStyles.Left, AutoSize = true }, 0, row);
        panel.Controls.Add(control1, 1, row);
        panel.Controls.Add(new Label { Text = label2, Anchor = AnchorStyles.Left, AutoSize = true }, 2, row);
        panel.Controls.Add(control2, 3, row);
    }
}
