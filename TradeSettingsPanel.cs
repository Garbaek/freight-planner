namespace FreightCopilot;

public sealed class TradeSettingsPanel : UserControl
{
    private readonly TradeContext _context;
    private readonly TextBox _token = Secret();
    private readonly TextBox _secret = Secret();
    private readonly Label _stored = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly Button _saveCredentials = TradeUi.Button("Save in Windows Credential Manager");
    private readonly Button _check = TradeUi.Button("Check with UEX");
    private readonly Button _remove = TradeUi.Button("Remove");
    private readonly CheckBox _test = new() { Text = "Send reports as a test: it tries the connection and reports nothing. Untick to report for real.", AutoSize = true };
    private readonly CheckBox _syncTrades = new() { Text = "Add Bought and Sold checkpoints to my personal UEX trade history", AutoSize = true };
    private readonly CheckBox _shareTrades = new() { Text = "Contribute completed runs to the website totals and top-10 leaderboard", AutoSize = true };
    private readonly CheckBox _shareUsage = new() { Text = "Share one anonymous app-launch count when Freight Planner opens (platform and version only)", AutoSize = true };
    private readonly Label _trackerStatus = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(22, 0, 3, 2) };
    private readonly TextBox _folder = new() { Width = 560 };
    private readonly Button _browse = TradeUi.Button("Browse…");
    private readonly CheckBox _watch = new() { Text = "While this window is open, read new screenshots and queue the commodity terminals on the Market reporter tab", AutoSize = true };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly NumericUpDown _maxAge = new() { Minimum = 1, Maximum = 24 * 30, Width = 70 };
    private bool _loading = true;

    public TradeSettingsPanel(TradeContext context)
    {
        _context = context;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        var settings = context.Settings;

        var account = Group("UEX account — needed to submit prices or personal trades", 196,
            Note("Reading prices needs no account. To report prices or keep trade history, UEX wants an app token (create an app at https://uexcorp.space/api/apps)\r\n" +
                 "and your personal secret key (uexcorp.space > Account > Secret Key). They are kept in Windows Credential Manager, encrypted for your\r\n" +
                 "Windows account, and are sent to api.uexcorp.uk only: when you press Check with UEX and when you approve a submission."),
            Row(Caption("App token", 90), _token, Caption("Secret key", 90), _secret),
            Row(_saveCredentials, _check, _remove, _stored));

        var submitting = Group("Submitting, community statistics and privacy", 228, _test, _syncTrades, _shareTrades, _trackerStatus, _shareUsage,
            Note("Price reports still show what goes out and wait for approval. Trade-history sync is different: enabling it makes each Bought or Sold\r\n" +
                 "checkmark the approval to send that side of the run, using its recorded terminal, commodity, SCU and price. Website statistics require\r\n" +
                 "a Freight Planner account and upload completed-run details to your Docker server. Anonymous launch counting stores only daily totals by platform and version."));

        var ocr = Group("Screenshots and OCR", 156,
            Row(Caption("Screenshot folder", 124), _folder, _browse),
            _watch,
            Note($"Text is read on this PC by {(context.Host.Ocr?.EngineDescription ?? "Windows OCR, which is unavailable on this PC")}. The game is never touched: the app only reads\r\n" +
                 "screenshot files you take yourself, and it shows new ones here without opening a window or taking the focus."));

        var prices = Group("Prices in Trade ideas", 104,
            Row(Caption("Use", 40), _mode),
            Row(Caption("Trust my own prices for", 164), _maxAge, Caption("hours. Older ones no longer replace UEX's and are marked stale.", 460)));

        var data = Group("Local data", 62, Note($"Observations and the trading journal are stored in {context.Store.PathOnDisk}"));

        // Docked from the bottom of the list up, so the first group ends on top.
        Controls.AddRange([data, prices, ocr, submitting, account]);

        _mode.Items.AddRange(["UEX reports and my own confirmed prices", "UEX reports only", "My own confirmed prices only"]);
        _mode.SelectedIndex = (int)settings.PriceMode;
        _maxAge.Value = Math.Clamp(settings.LocalPriceMaxAgeHours, (int)_maxAge.Minimum, (int)_maxAge.Maximum);
        _test.Checked = settings.SubmitAsTest;
        _syncTrades.Checked = settings.SyncTradesToUex;
        _shareTrades.Checked = settings.ShareTradeStats;
        _shareUsage.Checked = settings.ShareAnonymousUsage;
        _watch.Checked = settings.WatchTerminalScreenshots;
        _folder.Text = context.Host.ScreenshotFolder();
        ShowStored();
        _loading = false;

        _saveCredentials.Click += (_, _) => SaveCredentials();
        _check.Click += async (_, _) => await CheckAsync();
        _remove.Click += (_, _) => Remove();
        _browse.Click += (_, _) => Browse();
        _folder.Leave += (_, _) => { if (!_loading) context.Host.SetScreenshotFolder(_folder.Text.Trim()); };
        // The same choice sits on the Trade ideas tab; keep the two in step.
        Action follow = () =>
        {
            _loading = true;
            _mode.SelectedIndex = (int)settings.PriceMode;
            _loading = false;
        };
        context.Changed += follow;
        Disposed += (_, _) => context.Changed -= follow;
        _mode.SelectedIndexChanged += (_, _) => Apply();
        _maxAge.ValueChanged += (_, _) => Apply();
        foreach (var box in new[] { _test, _syncTrades, _shareUsage, _watch }) box.CheckedChanged += (_, _) => Apply();
        _shareTrades.CheckedChanged += async (_, _) =>
        {
            if (_loading) return;
            Apply();
            _shareTrades.Enabled = false;
            _trackerStatus.Text = "Updating the server preference…";
            try { _trackerStatus.Text = await context.Host.SetTradeTracker(_shareTrades.Checked); }
            finally { _shareTrades.Enabled = true; }
        };
    }

    private static TextBox Secret() => new() { Width = 300, UseSystemPasswordChar = true, Margin = new Padding(3, 3, 14, 3) };

    private static Label Caption(string text, int width) => new() { Text = text, Width = width, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 3, 3, 3) };

    private static Label Note(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 4, 3, 6) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    private static GroupBox Group(string title, int height, params Control[] rows)
    {
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8, 2, 8, 2) };
        body.Controls.AddRange(rows);
        var group = new GroupBox { Text = title, Dock = DockStyle.Top, Height = height, Padding = new Padding(8), Margin = new Padding(10) };
        group.Controls.Add(body);
        return group;
    }

    private void Apply()
    {
        if (_loading) return;
        var settings = _context.Settings;
        settings.PriceMode = (PriceMode)_mode.SelectedIndex;
        settings.LocalPriceMaxAgeHours = (int)_maxAge.Value;
        settings.SubmitAsTest = _test.Checked;
        settings.SyncTradesToUex = _syncTrades.Checked;
        settings.ShareTradeStats = _shareTrades.Checked;
        settings.ShareAnonymousUsage = _shareUsage.Checked;
        settings.WatchTerminalScreenshots = _watch.Checked;
        _context.Host.SaveSettings();
        _context.NotifyChanged();
    }

    private void Browse()
    {
        using var picker = new FolderBrowserDialog { Description = "Star Citizen screenshot folder", UseDescriptionForTitle = true };
        if (Directory.Exists(_folder.Text)) picker.InitialDirectory = _folder.Text;
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _folder.Text = picker.SelectedPath;
        _context.Host.SetScreenshotFolder(picker.SelectedPath);
    }

    private void ShowStored()
    {
        var token = CredentialVault.Read(CredentialVault.UexAppToken) is { Length: > 0 };
        var secret = CredentialVault.Read(CredentialVault.UexSecretKey) is { Length: > 0 };
        // What is stored is never shown again, not even masked.
        _token.PlaceholderText = token ? "stored — type to replace" : "not set";
        _secret.PlaceholderText = secret ? "stored — type to replace" : "not set";
        _stored.Text = token && secret ? "Both are stored ✓" : token ? "The secret key is missing." : secret ? "The app token is missing." : "Nothing stored.";
        _stored.ForeColor = token && secret ? Color.FromArgb(23, 128, 93) : Color.DimGray;
        _check.Enabled = _remove.Enabled = token || secret;
    }

    private void SaveCredentials()
    {
        try
        {
            if (_token.Text.Trim() is { Length: > 0 } token) CredentialVault.Write(CredentialVault.UexAppToken, token);
            if (_secret.Text.Trim() is { Length: > 0 } secret) CredentialVault.Write(CredentialVault.UexSecretKey, secret);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "UEX account", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        _token.Clear();
        _secret.Clear();
        ShowStored();
    }

    private void Remove()
    {
        if (MessageBox.Show(this, "Remove the UEX app token and secret key from Windows Credential Manager?", "UEX account",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        CredentialVault.Delete(CredentialVault.UexAppToken);
        CredentialVault.Delete(CredentialVault.UexSecretKey);
        ShowStored();
    }

    private async Task CheckAsync()
    {
        if (CredentialVault.Uex() is not { } credentials)
        {
            _stored.Text = "Store both the app token and the secret key first.";
            return;
        }
        _check.Enabled = false;
        _stored.ForeColor = Color.DimGray;
        _stored.Text = "Asking UEX…";
        try
        {
            var user = await _context.Uex.UserAsync(credentials, CancellationToken.None);
            // UEX takes reports from data runners only, and its user lookup says whether the account is one.
            // The lookup judges the secret key alone; the app token is first judged when a report goes in.
            var ready = user.IsDatarunner == 1 && user.IsDatarunnerBanned != 1;
            _stored.Text = user.IsDatarunnerBanned == 1 ? $"UEX knows you as {user.Username}, but the account is barred from reporting."
                : user.IsDatarunner != 1 ? $"Secret key accepted (UEX knows you as {user.Username}), but the account is not a data runner yet, so UEX will refuse reports. Become one at uexcorp.space first."
                : $"Secret key accepted: UEX knows you as {user.Username}, a data runner ✓  Send one test report to check the app token too.";
            _stored.ForeColor = ready ? Color.FromArgb(23, 128, 93) : Color.Firebrick;
        }
        catch (UexException ex)
        {
            _stored.Text = ex.Message;
            _stored.ForeColor = Color.Firebrick;
        }
        finally { _check.Enabled = true; }
    }
}
