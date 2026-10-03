using QRCoder;

namespace FreightCopilot;

public sealed class PairingForm : Form
{
    public PairingForm(string apiBaseUrl, PairingRegistration registration)
    {
        // A server from before QR linking returns no link token; a QR without one could never be accepted.
        var hasQr = !string.IsNullOrEmpty(registration.LinkToken);
        Text = "Pair Freight Planner";
        ClientSize = hasQr ? new Size(420, 580) : new Size(420, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = hasQr
                ? "On the tablet, tap SCAN PC QR CODE and point the camera here. Scanning signs the tablet in and links it; no typing needed."
                : "Sign in on the tablet with this account, then enter the code below.",
            AutoSize = true, MaximumSize = new Size(388, 0), Padding = new Padding(0, 0, 0, 8)
        }, 0, 0);
        layout.Controls.Add(hasQr
            ? new PictureBox
            {
                Image = CreateQrImage(LinkUri(apiBaseUrl, registration.LinkToken)),
                SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.White
            }
            : new Label
            {
                Text = "QR linking is unavailable: the account server has not been updated for it yet.",
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Firebrick
            }, 0, 1);
        layout.Controls.Add(new Label
        {
            Text = hasQr ? "Or, on a tablet that is already signed in, enter this code:" : "Pairing code:",
            AutoSize = true, Padding = new Padding(0, 10, 0, 0)
        }, 0, 2);
        layout.Controls.Add(new Label
        {
            Text = registration.Code, AutoSize = true, Anchor = AnchorStyles.None,
            Font = new Font("Consolas", 26, FontStyle.Bold)
        }, 0, 3);
        layout.Controls.Add(new Label
        {
            // Kept to one line: a wrapped last row was clipped by the fixed-size window.
            Text = $"Expires {registration.ExpiresAtUtc.ToLocalTime():HH:mm}. Works once. Closes when the tablet connects.",
            AutoSize = true, Padding = new Padding(0, 0, 0, 6), ForeColor = SystemColors.GrayText
        }, 0, 4);
        Controls.Add(layout);
    }

    public static string LinkUri(string apiBaseUrl, string linkToken) =>
        $"freightplanner://link?server={Uri.EscapeDataString(apiBaseUrl.Trim().TrimEnd('/'))}&token={Uri.EscapeDataString(linkToken)}";

    private static Image CreateQrImage(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        // The bitmap must own its pixels because the stream is disposed here.
        using var stream = new MemoryStream(png);
        using var decoded = Image.FromStream(stream);
        return new Bitmap(decoded);
    }
}
