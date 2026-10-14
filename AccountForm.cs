using System.ComponentModel;
using System.Diagnostics;

namespace FreightCopilot;

public sealed class AccountForm : Form
{
    private readonly TextBox _server = new() { Dock = DockStyle.Fill };
    private readonly TextBox _displayName = new() { Dock = DockStyle.Fill };
    private readonly TextBox _email = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Color.Firebrick };
    private readonly Button _resend = new() { Text = "Resend verification email", AutoSize = true, Visible = false };
    private readonly Button _submit = new() { Text = "Sign in", AutoSize = true };
    private readonly Button _switchMode = new() { Text = "Create an account", AutoSize = true };
    private readonly Label _displayLabel = new() { Text = "Display name", AutoSize = true, Anchor = AnchorStyles.Left };

    public AuthSession? Session { get; private set; }
    public string ApiBaseUrl => _server.Text.Trim();

    public AccountForm(string apiBaseUrl)
    {
        Text = "Freight Planner account";
        Width = 520;
        Height = 390;
        MinimumSize = new Size(440, 330);
        StartPosition = FormStartPosition.CenterParent;
        _server.Text = apiBaseUrl;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 6
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(layout, 0, new Label { Text = "Server", AutoSize = true, Anchor = AnchorStyles.Left }, _server);
        AddRow(layout, 1, _displayLabel, _displayName);
        AddRow(layout, 2, new Label { Text = "Email", AutoSize = true, Anchor = AnchorStyles.Left }, _email);
        AddRow(layout, 3, new Label { Text = "Password", AutoSize = true, Anchor = AnchorStyles.Left }, _password);
        layout.Controls.Add(_message, 1, 4);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([_submit, _switchMode, new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel }, _resend]);
        layout.Controls.Add(buttons, 1, 5);
        Controls.Add(layout);

        _submit.Click += async (_, _) => await SubmitAsync();
        _resend.Click += async (_, _) => await ResendAsync();
        _switchMode.Click += (_, _) => OpenRegistrationPage();
        AcceptButton = _submit;
        CancelButton = buttons.Controls.OfType<Button>().First(x => x.DialogResult == DialogResult.Cancel);
        ShowSignInMode();
    }

    private static void AddRow(TableLayoutPanel panel, int row, Control label, Control value)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.Controls.Add(label, 0, row);
        panel.Controls.Add(value, 1, row);
    }

    private void ShowSignInMode()
    {
        _displayLabel.Visible = _displayName.Visible = false;
        _submit.Text = "Sign in";
        _switchMode.Text = "Create account on website";
        _message.Text = "New accounts use privacy-friendly ALTCHA verification on the website.";
        _message.ForeColor = SystemColors.GrayText;
    }

    private void OpenRegistrationPage()
    {
        if (!Uri.TryCreate(_server.Text.Trim().TrimEnd('/') + "/account", UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)))
        {
            _message.ForeColor = Color.Firebrick;
            _message.Text = "Use a valid HTTPS server URL first.";
            return;
        }
        if (!string.IsNullOrWhiteSpace(_email.Text))
            uri = new UriBuilder(uri) { Query = "email=" + Uri.EscapeDataString(_email.Text.Trim()) }.Uri;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _message.ForeColor = Color.Firebrick;
            _message.Text = $"Could not open the browser. Open {uri.AbsoluteUri} yourself.";
            return;
        }
        _resend.Visible = true;
        _message.ForeColor = Color.SeaGreen;
        _message.Text = "The secure account page was opened in your browser. Verify the email, then sign in here.";
    }

    private async Task SubmitAsync()
    {
        _message.ForeColor = Color.Firebrick;
        if (!Uri.TryCreate(_server.Text.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)))
        {
            _message.Text = "Use an HTTPS server URL (HTTP is allowed only for localhost development).";
            return;
        }
        if (string.IsNullOrWhiteSpace(_email.Text) || string.IsNullOrEmpty(_password.Text))
        {
            _message.Text = "Email and password are required.";
            return;
        }
        SetBusy(true);
        try
        {
            var client = new AccountClient(_server.Text);
            Session = await client.LoginAsync(_email.Text.Trim(), _password.Text, CancellationToken.None);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is AccountApiException or HttpRequestException or TaskCanceledException)
        {
            SetBusy(false);
            _message.Text = ex is TaskCanceledException ? "The account server did not respond in time." : ex.Message;
            // 403 on sign-in means the password was right but the email link has not been opened yet.
            if (ex is AccountApiException { StatusCode: 403 }) _resend.Visible = true;
        }
        finally { SetBusy(false); }
    }

    private async Task ResendAsync()
    {
        if (string.IsNullOrWhiteSpace(_email.Text))
        {
            _message.ForeColor = Color.Firebrick;
            _message.Text = "Enter the account email first.";
            return;
        }
        _resend.Enabled = false;
        try
        {
            var result = await new AccountClient(_server.Text).ResendVerificationAsync(_email.Text.Trim(), CancellationToken.None);
            ShowInfo(result.Message);
        }
        catch (Exception ex) when (ex is AccountApiException or HttpRequestException or TaskCanceledException or UriFormatException)
        {
            _message.ForeColor = Color.Firebrick;
            _message.Text = ex is TaskCanceledException ? "The account server did not respond in time." : ex.Message;
        }
        finally { _resend.Enabled = true; }
    }

    private void ShowInfo(string text)
    {
        _message.ForeColor = Color.SeaGreen;
        _message.Text = text;
    }

    private void SetBusy(bool busy)
    {
        _submit.Enabled = _switchMode.Enabled = !busy;
        UseWaitCursor = busy;
        if (busy)
        {
            _message.ForeColor = SystemColors.GrayText;
            _message.Text = "Signing in…";
        }
    }
}

