using System.Text.Json;

namespace FreightCopilot;

// Sends confirmed observations to UEX, and only after the player has seen exactly what goes out and approved it.
public static class UexSubmitFlow
{
    // Returns one line for the status bar saying what happened.
    public static async Task<string> RunAsync(IWin32Window owner, TradeContext context, IReadOnlyList<MarketObservation> rows)
    {
        if (rows.Count == 0) return "Nothing to send: save and confirm observations first.";
        if (CredentialVault.Uex() is not { } credentials)
        {
            MessageBox.Show(owner, "UEX needs to know who is reporting. Add your UEX app token and secret key on the Settings tab first.",
                "Submit to UEX", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return "Nothing was sent to UEX: no credentials are stored.";
        }
        // Rows UEX would not want are left at home and named; the others go on.
        var held = UexSubmissionBuilder.Held(rows, context.Store.LastSubmission, DateTime.UtcNow);
        var leftOut = UexSubmissionBuilder.Blockers(rows, context.Store.LastSubmission, DateTime.UtcNow);
        rows = rows.Where(x => !held.ContainsKey(x)).ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(owner, string.Join(Environment.NewLine + Environment.NewLine, leftOut.Take(8)) + (leftOut.Count > 8 ? $"{Environment.NewLine}{Environment.NewLine}…and {leftOut.Count - 8} more." : ""),
                "None of these rows can go to UEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return "Nothing was sent to UEX.";
        }

        using var confirm = new SubmitConfirmForm(rows, leftOut, context.Settings);
        if (confirm.ShowDialog(owner) != DialogResult.OK) return "Nothing was sent to UEX.";

        var sent = 0;
        string? failure = null;
        foreach (var (submission, batch) in UexSubmissionBuilder.Build(rows, confirm.Production, attachScreenshot: true, DateTime.UtcNow))
        {
            UexSubmitResult result;
            try { result = await context.Uex.SubmitAsync(submission, credentials, CancellationToken.None); }
            catch (UexException ex) { result = new UexSubmitResult(false, "unreachable", ex.Message); }

            var state = !result.Ok ? SubmissionState.Failed : confirm.Production ? SubmissionState.Submitted : SubmissionState.Test;
            var detail = !result.Ok ? result.Message
                : confirm.Production ? $"UEX report {result.ReportIds ?? "accepted"}" : "Accepted by UEX as a test; not a real report.";
            DateTime? when = result.Ok ? DateTime.UtcNow : null;
            context.Store.SetSubmission(batch.Select(x => x.Id), state, detail, when);
            foreach (var row in batch) (row.Submission, row.SubmissionDetail, row.SubmittedUtc) = (state, detail, when);
            // A refusal is about the account, the limit or the connection far more often than about one row,
            // so the rest waits instead of being fired at UEX as well.
            if (!result.Ok) { failure = result.Message; break; }
            sent += batch.Count;
        }
        context.NotifyChanged();
        // UEX staff review reports before they show up, and may decline or edit them.
        var what = confirm.Production ? "sent to UEX, which reviews reports before they appear" : "accepted by UEX as a test (not a real report)";
        var kept = leftOut.Count > 0 ? $" {leftOut.Count:N0} row(s) were left out; they stay on this PC." : "";
        return failure is null ? $"{sent:N0} observation(s) {what}.{kept}"
            : sent == 0 ? $"Nothing was sent. UEX refused: {failure}"
            : $"{sent:N0} of {rows.Count:N0} observation(s) {what}. Then UEX refused: {failure}";
    }
}

public sealed class SubmitConfirmForm : Form
{
    private readonly IReadOnlyList<MarketObservation> _rows;
    private readonly CheckBox _test = new() { Text = "Send as a test: tries the connection, reports nothing", AutoSize = true };

    private readonly Label _mode = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 11), Margin = new Padding(3, 8, 3, 6) };
    private readonly TextBox _body = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9) };
    private readonly Button _send = new() { AutoSize = true };

    public bool Production => !_test.Checked;

    public SubmitConfirmForm(IReadOnlyList<MarketObservation> rows, IReadOnlyList<string> leftOut, TradeSettings settings)
    {
        _rows = rows;
        Text = "Freight Planner — Submit to UEX";
        Width = 980;
        Height = 700;
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = TradeUi.Dark };
        header.Controls.Add(new Label { Text = "SEND THESE PRICES TO UEX?", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 18), AutoSize = true, Location = new Point(20, 11) });
        header.Controls.Add(new Label { Text = "Nothing leaves this PC until you press the send button. Check every number against the terminal first.", ForeColor = TradeUi.Muted, AutoSize = true, Location = new Point(22, 44) });

        var grid = TradeUi.Grid();
        grid.Columns.Add(TradeUi.Text("Terminal", 190));
        grid.Columns.Add(TradeUi.Fill("Commodity", 150));
        grid.Columns.Add(TradeUi.Text("Side", 90));
        grid.Columns.Add(TradeUi.Number("Price / SCU", 90, format: "#,0.##"));
        grid.Columns.Add(TradeUi.Number("Shop SCU", 80));
        grid.Columns.Add(TradeUi.Text("Status", 100));
        grid.Columns.Add(TradeUi.Text("Crates", 120));
        grid.Columns.Add(TradeUi.Text("Seen", 110));
        foreach (var row in rows)
            grid.Rows.Add(row.TerminalName, row.CommodityName, row.IsBuy ? "You buy" : "You sell", row.Price, (double?)row.StockScu, TradeUi.Status(row.Status),
                row.ContainerSizes, TradeUi.When(row.ObservedUtc));

        _test.Checked = settings.SubmitAsTest;
        var shown = leftOut.Take(4).ToList();
        var notes = new List<string> { "Each report goes with its screenshot: UEX takes none without. Is the terminal above the one you were at, and does every number match the picture?" };
        if (leftOut.Count > 0) notes.Add($"Left out, and staying on this PC ({leftOut.Count:N0}):");
        notes.AddRange(shown.Select(x => "   " + x));
        if (leftOut.Count > shown.Count) notes.Add($"   …and {leftOut.Count - shown.Count:N0} more.");
        var note = new Label { Text = string.Join(Environment.NewLine, notes), AutoSize = true, ForeColor = leftOut.Count > 0 ? Color.FromArgb(150, 45, 35) : Color.DimGray, Margin = new Padding(3, 6, 3, 0) };
        var options = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 94 + notes.Count * 19, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10, 4, 10, 0), WrapContents = false };
        options.Controls.AddRange([_mode, _test, note]);

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _send.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = Color.White };
        footer.Controls.AddRange([cancel, _send]);

        var split = TradeUi.Split(Orientation.Horizontal, firstPanel: 230);
        split.Panel1.Controls.Add(grid);
        split.Panel2.Controls.Add(_body);
        split.Panel2.Controls.Add(new Label { Text = "Exactly what is sent", Dock = DockStyle.Top, Height = 24, Padding = new Padding(4, 4, 0, 0) });

        Controls.Add(split);
        Controls.Add(options);
        Controls.Add(footer);
        Controls.Add(header);
        // Enter must not send by accident.
        CancelButton = cancel;
        ActiveControl = cancel;
        _test.CheckedChanged += (_, _) => ShowWhatIsSent();
        ShowWhatIsSent();
        Shown += (_, _) => grid.ClearSelection();
    }

    private void ShowWhatIsSent()
    {
        _mode.Text = Production
            ? "REAL REPORT: these prices go to UEX under your account. UEX reviews reports and may decline or edit them."
            : "TEST: tries the connection. Nothing is reported.";
        _mode.ForeColor = Production ? Color.Firebrick : Color.FromArgb(23, 128, 93);
        _send.Text = Production ? "Send to UEX" : "Send test to UEX";
        var pretty = new JsonSerializerOptions { WriteIndented = true };
        // The picture itself is megabytes of base64, so it is named here instead of printed.
        _body.Text = string.Join(Environment.NewLine + Environment.NewLine, UexSubmissionBuilder.Build(_rows, Production, attachScreenshot: false, DateTime.UtcNow).Select(batch =>
        {
            using var json = JsonDocument.Parse(UexClient.Serialize(batch.Submission));
            var shot = batch.Rows[0].ScreenshotPath;
            return "POST https://api.uexcorp.uk/2.0/data_submit" + Environment.NewLine + JsonSerializer.Serialize(json.RootElement, pretty) +
                   (File.Exists(shot) ? $"{Environment.NewLine}plus \"screenshot\": {Path.GetFileName(shot)} ({new FileInfo(shot).Length / 1024:N0} KB)" : "");
        }));
    }
}
