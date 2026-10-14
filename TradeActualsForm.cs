namespace FreightCopilot;

// A deliberately small confirmation sheet used by both purchase and sale checkpoints. Planned values
// are prefilled, but the journal only receives what the player confirms actually happened at the terminal.
public sealed class TradeActualsForm : Form
{
    private readonly NumericUpDown _scu = new() { Minimum = 1, Maximum = 10_000_000, ThousandsSeparator = true, Width = 160 };
    private readonly NumericUpDown _price = new() { Minimum = 0.01m, Maximum = 10_000_000, DecimalPlaces = 2, ThousandsSeparator = true, Width = 160 };
    private readonly ComboBox _terminal = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 430 };

    public TradeActualsForm(TradeRun run, bool purchase)
    {
        Text = purchase ? "Confirm actual purchase" : "Record cargo sale";
        Width = 610;
        Height = purchase ? 275 : 330;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);

        _scu.Maximum = purchase ? run.Scu : Math.Max(1, run.RemainingScu);
        _scu.Value = purchase ? run.Scu : Math.Max(1, run.RemainingScu);
        _price.Value = (decimal)(purchase ? run.PriceBuy : run.SaleOptions.FirstOrDefault()?.Price ?? run.PriceSell);
        _terminal.Visible = !purchase;
        if (!purchase)
        {
            _terminal.Items.AddRange(run.SaleOptions.Cast<object>().ToArray());
            _terminal.DisplayMember = nameof(TradeSaleOption.Place);
            _terminal.SelectedIndex = Math.Max(0, run.SaleOptions.ToList().FindIndex(x => x.TerminalId == run.DestinationId));
            _terminal.SelectedIndexChanged += (_, _) =>
            {
                if (_terminal.SelectedItem is TradeSaleOption option) _price.Value = (decimal)Math.Clamp(option.Price, 0.01, 10_000_000);
            };
        }

        var explanation = new Label
        {
            AutoSize = true, MaximumSize = new Size(540, 0),
            Text = purchase
                ? $"The route planned {run.Scu:N0} SCU at {run.PriceBuy:N2} aUEC/SCU. Enter what the terminal actually sold you."
                : $"{run.RemainingScu:N0} SCU remain from this cargo lot. Record this transaction; you can sell the rest at this or another buyer later."
        };
        var fields = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = purchase ? 2 : 3 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var row = 0;
        if (!purchase) Add("Buyer", _terminal);
        Add("Actual SCU", _scu);
        Add("Price / SCU", _price);

        var ok = new Button { Text = purchase ? "Confirm purchase" : "Record sale", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, ok]);
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(18) };
        body.Controls.AddRange([explanation, fields, buttons]);
        Controls.Add(body);
        AcceptButton = ok;
        CancelButton = cancel;

        void Add(string label, Control control)
        {
            fields.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 9, 8, 0) }, 0, row);
            fields.Controls.Add(control, 1, row++);
        }
    }

    public int Scu => (int)_scu.Value;
    public double Price => (double)_price.Value;
    public TradeSaleOption? Terminal => _terminal.SelectedItem as TradeSaleOption;
}
