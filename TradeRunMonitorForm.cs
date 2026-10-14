namespace FreightCopilot;

// The Windows counterpart of the Android trade screen. Both surfaces operate on the same TradeRun;
// the main form publishes every accepted Windows change back to the tablet.
public sealed class TradeRunMonitorForm : Form
{
    private readonly TradeRun _run;
    private readonly Func<TradeStep, bool, Task<string>> _change;
    private readonly Dictionary<string, CheckBox> _checks = [];
    private readonly Dictionary<string, Label> _times = [];
    private readonly Label _totals = new() { Dock = DockStyle.Top, Height = 66, Padding = new Padding(16, 12, 16, 8), Font = new Font("Consolas", 12, FontStyle.Bold), ForeColor = Color.FromArgb(243, 235, 201), BackColor = Color.FromArgb(16, 53, 59) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(14, 11, 14, 4), BackColor = Color.White };
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    private bool _updating;

    public TradeRunMonitorForm(TradeRun run, Func<TradeStep, bool, Task<string>> change)
    {
        _run = run;
        _change = change;
        Text = "Freight Planner — Trade Run Monitor";
        Width = 980;
        Height = 760;
        MinimumSize = new Size(760, 580);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);

        var title = new Label { Text = "TRADE RUN", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 18), AutoSize = true, Location = new Point(20, 10) };
        var subtitle = new Label
        {
            Text = $"{run.Ship}  //  PRICES: {run.Source.ToUpperInvariant()}  //  CONFIDENCE: {run.Confidence.ToUpperInvariant()}",
            ForeColor = TradeUi.Muted, AutoSize = true, Location = new Point(22, 44)
        };
        var header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = TradeUi.Dark };
        header.Controls.AddRange([title, subtitle]);

        var cards = new TableLayoutPanel { Dock = DockStyle.Top, Height = 176, ColumnCount = 2, Padding = new Padding(10, 10, 10, 6), BackColor = Color.FromArgb(238, 243, 244) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.Controls.Add(Card("BUY", Color.FromArgb(36, 137, 147), $"{run.Scu:N0} SCU {run.Commodity}",
            $"AT {Place(run.Origin, run.OriginSystem)}\r\n{Detail(run.OriginDetail)}{run.PriceBuy:N2} aUEC / SCU   //   OUTLAY {run.Scu * run.PriceBuy:N0} aUEC\r\nCRATES   {run.Crates}"), 0, 0);
        cards.Controls.Add(Card("HAUL TO AND SELL AT", Color.FromArgb(151, 117, 29), Place(run.Destination, run.DestinationSystem),
            $"{Detail(run.DestinationDetail)}{run.PriceSell:N2} aUEC / SCU   //   SALE {run.Scu * run.PriceSell:N0} aUEC\r\nPLANNED PROFIT {run.Profit:N0} aUEC" +
            (run.Distance is { } distance ? $"   //   {distance:N0} GM" : "")), 1, 0);

        var checklist = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Padding = new Padding(16, 8, 16, 12), BackColor = Color.FromArgb(8, 25, 29)
        };
        checklist.Controls.Add(new Label
        {
            Text = $"STARTED {run.StartedUtc.ToLocalTime():HH:mm:ss}   //   TICK EACH STEP AS YOU FINISH IT",
            AutoSize = true, ForeColor = Color.FromArgb(143, 177, 182), Font = new Font("Consolas", 10, FontStyle.Bold), Margin = new Padding(4, 4, 4, 8)
        });
        foreach (var step in run.Steps)
        {
            var row = new TableLayoutPanel { Width = 900, Height = 46, ColumnCount = 2, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            var check = new CheckBox
            {
                Text = step.Label.ToUpperInvariant(), Dock = DockStyle.Fill, ForeColor = Color.FromArgb(216, 231, 231),
                Font = new Font("Segoe UI Semibold", 11), Padding = new Padding(4, 0, 0, 0), Tag = step
            };
            var time = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(124, 211, 177),
                Font = new Font("Consolas", 10, FontStyle.Bold), Padding = new Padding(0, 0, 6, 0)
            };
            check.CheckedChanged += async (_, _) => await ChangedAsync(check, step);
            _checks[step.Id] = check;
            _times[step.Id] = time;
            row.Controls.Add(check, 0, 0);
            row.Controls.Add(time, 1, 0);
            checklist.Controls.Add(row);
        }

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 8), BackColor = Color.FromArgb(238, 243, 244) };
        body.Controls.Add(checklist);
        body.Controls.Add(_totals);
        Controls.Add(body);
        Controls.Add(cards);
        Controls.Add(_status);
        Controls.Add(header);

        _clock.Tick += (_, _) => RefreshFromRun();
        _clock.Start();
        FormClosed += (_, _) => _clock.Stop();
        RefreshFromRun("The Windows and tablet checklists monitor the same run.");
    }

    public TradeRun Run => _run;

    public void RefreshFromRun(string? message = null)
    {
        if (IsDisposed) return;
        _updating = true;
        var timeline = _run.Timeline().ToDictionary(x => x.Step.Id);
        foreach (var step in _run.Steps)
        {
            var done = _run.Done.ContainsKey(step.Id);
            var other = !done && step.Alternative.Length > 0 && _run.Steps.Any(x => x.Id != step.Id && x.Alternative == step.Alternative && _run.Done.ContainsKey(x.Id));
            _checks[step.Id].Checked = done;
            _checks[step.Id].ForeColor = done ? Color.FromArgb(124, 211, 177) : other ? Color.FromArgb(102, 135, 141) : Color.FromArgb(216, 231, 231);
            _times[step.Id].Text = timeline.TryGetValue(step.Id, out var tick) ? $"{tick.Utc.ToLocalTime():HH:mm:ss}   +{Span(tick.Seconds)}" : "";
        }
        _updating = false;

        var end = _run.Done.GetValueOrDefault(TradeRun.Sold, DateTime.UtcNow);
        var seconds = Math.Max(0, (int)Math.Round((end - _run.StartedUtc).TotalSeconds));
        var rate = seconds < 60 ? "RATE AFTER THE FIRST MINUTE" : $"{_run.Profit * 3600 / seconds:N0} aUEC / H   //   {_run.Profit * 60 / seconds:N0} aUEC / MIN";
        _totals.Text = _run.IsCompleted
            ? $"RUN COMPLETE   {Span(seconds)}   //   PLANNED PROFIT {_run.Profit:N0} aUEC\r\n{rate}"
            : $"TIME SPENT   {Span(seconds)}\r\nIF SOLD NOW   {rate}";
        if (message is not null) _status.Text = message;
    }

    private async Task ChangedAsync(CheckBox check, TradeStep step)
    {
        if (_updating) return;
        check.Enabled = false;
        try { RefreshFromRun(await _change(step, check.Checked)); }
        catch (Exception ex) { RefreshFromRun($"The checkpoint could not be saved: {ex.Message}"); }
        finally { check.Enabled = true; }
    }

    private static Panel Card(string heading, Color accent, string headline, string lines)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(5), BackColor = Color.FromArgb(20, 31, 46), Padding = new Padding(14) };
        panel.Controls.Add(new Label { Text = lines.ToUpperInvariant(), Dock = DockStyle.Fill, ForeColor = Color.FromArgb(216, 231, 231), Padding = new Padding(0, 8, 0, 0) });
        panel.Controls.Add(new Label { Text = headline.ToUpperInvariant(), Dock = DockStyle.Top, Height = 34, ForeColor = Color.FromArgb(243, 235, 201), Font = new Font("Segoe UI Semibold", 15) });
        panel.Controls.Add(new Label { Text = heading, Dock = DockStyle.Top, Height = 22, ForeColor = accent, Font = new Font("Consolas", 10, FontStyle.Bold) });
        return panel;
    }

    private static string Place(string name, string system) => system.Length > 0 ? $"{name} — {system}" : name;
    private static string Detail(string detail) => detail.Length > 0 ? detail.ToUpperInvariant() + "\r\n" : "";
    private static string Span(int seconds) => seconds >= 3600 ? $"{seconds / 3600}:{seconds % 3600 / 60:D2}:{seconds % 60:D2}" : $"{seconds / 60}:{seconds % 60:D2}";
}
