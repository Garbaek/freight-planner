using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace FreightCopilot;

public sealed class TravelMapForm : Form
{
    private readonly TravelTimeDatabase _database;
    private readonly BindingList<TravelTimeLeg> _rows;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AutoGenerateColumns = false,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        BackgroundColor = Color.White
    };
    private readonly RouteMapCanvas _map;

    public TravelMapForm(TravelTimeDatabase database, ManifestResult? manifest)
    {
        _database = database;
        var route = manifest?.LocalRoute ?? [];
        _rows = new BindingList<TravelTimeLeg>(BuildRows(database, manifest));
        _map = new RouteMapCanvas(database, route) { Dock = DockStyle.Fill };

        Text = "Freight Planner — Travel Map & Times";
        Width = 1120;
        Height = 820;
        MinimumSize = new Size(820, 620);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Color.FromArgb(20, 31, 46) };
        header.Controls.Add(new Label
        {
            Text = "TRAVEL MAP & TIME TABLE",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 18),
            AutoSize = true,
            Location = new Point(20, 12)
        });
        header.Controls.Add(new Label
        {
            Text = "Enter realistic door-to-door flight minutes. Saved values replace defaults in route ordering and deadline estimates.",
            ForeColor = Color.FromArgb(188, 204, 220),
            AutoSize = true,
            Location = new Point(22, 49)
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "From", DataPropertyName = nameof(TravelTimeLeg.From), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 42 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "To", DataPropertyName = nameof(TravelTimeLeg.To), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 42 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Minutes", DataPropertyName = nameof(TravelTimeLeg.Minutes), Width = 85 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Both ways", DataPropertyName = nameof(TravelTimeLeg.Bidirectional), Width = 85 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ship / source", DataPropertyName = nameof(TravelTimeLeg.ShipName), Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Samples", DataPropertyName = nameof(TravelTimeLeg.SampleCount), Width = 70, ReadOnly = true });
        _grid.DataSource = _rows;
        _grid.DataError += (_, _) => { };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 350 };
        split.Panel1.Controls.Add(_map);
        split.Panel2.Controls.Add(_grid);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
            BackColor = Color.White
        };
        var close = new Button { Text = "Close", AutoSize = true };
        var save = new Button { Text = "Save travel times", AutoSize = true };
        var openJson = new Button { Text = "Open JSON", AutoSize = true };
        close.Click += (_, _) => Close();
        save.Click += (_, _) => SaveRows();
        openJson.Click += (_, _) => Process.Start(new ProcessStartInfo(_database.PathOnDisk) { UseShellExecute = true });
        footer.Controls.AddRange([close, save, openJson]);

        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(header);
    }

    private void SaveRows()
    {
        _grid.EndEdit();
        var invalid = _rows.Where(x => string.IsNullOrWhiteSpace(x.From) || string.IsNullOrWhiteSpace(x.To) || x.Minutes <= 0).ToList();
        if (invalid.Count > 0)
        {
            MessageBox.Show("Every saved row needs From, To, and a positive minute value.", "Travel times", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            _database.ReplaceLegs(_rows);
            _map.Invalidate();
            MessageBox.Show("Travel times saved. The route planner will use them immediately.", "Travel times", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not save travel times", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static List<TravelTimeLeg> BuildRows(TravelTimeDatabase database, ManifestResult? manifest)
    {
        var rows = database.Settings.Legs.Select(x => new TravelTimeLeg
        {
            From = x.From, To = x.To, Minutes = x.Minutes, Bidirectional = x.Bidirectional,
            ShipName = x.ShipName, SampleCount = x.SampleCount, TotalMinutes = x.TotalMinutes
        }).ToList();
        if (manifest is null) return rows;

        var stops = manifest.Stops.Select(ManifestOptimizer.MapLocationLabel).DistinctBy(ManifestOptimizer.NormalizeLocation).ToList();
        for (var i = 0; i < stops.Count; i++)
        for (var j = i + 1; j < stops.Count; j++)
        {
            if (HasPair(rows, stops[i], stops[j])) continue;
            rows.Add(new TravelTimeLeg
            {
                From = stops[i], To = stops[j], Minutes = database.Estimate(stops[i], stops[j]).Minutes, Bidirectional = true
            });
        }
        return rows.OrderBy(x => x.From).ThenBy(x => x.To).ToList();
    }

    private static bool HasPair(IEnumerable<TravelTimeLeg> rows, string from, string to)
    {
        var a = ManifestOptimizer.NormalizeLocation(from);
        var b = ManifestOptimizer.NormalizeLocation(to);
        return rows.Any(x =>
            (ManifestOptimizer.NormalizeLocation(x.From) == a && ManifestOptimizer.NormalizeLocation(x.To) == b) ||
            (x.Bidirectional && ManifestOptimizer.NormalizeLocation(x.From) == b && ManifestOptimizer.NormalizeLocation(x.To) == a));
    }
}

internal sealed class RouteMapCanvas : Control
{
    private readonly TravelTimeDatabase _database;
    private readonly IReadOnlyList<string> _route;

    public RouteMapCanvas(TravelTimeDatabase database, IReadOnlyList<string> route)
    {
        _database = database;
        _route = route;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(241, 246, 250);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var route = _route.Count > 0 ? _route : _database.Settings.Legs.SelectMany(x => new[] { x.From, x.To }).DistinctBy(ManifestOptimizer.NormalizeLocation).Take(8).ToList();
        var nodes = route.DistinctBy(ManifestOptimizer.NormalizeLocation).ToList();
        if (nodes.Count == 0)
        {
            TextRenderer.DrawText(e.Graphics, "No route yet. Build a contract plan, then reopen this window.", Font,
                ClientRectangle, Color.FromArgb(72, 88, 104), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var center = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f + 8);
        var radiusX = Math.Max(120, ClientSize.Width * 0.36f);
        var radiusY = Math.Max(70, ClientSize.Height * 0.30f);
        var points = new Dictionary<string, PointF>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < nodes.Count; i++)
        {
            var angle = -Math.PI / 2 + i * Math.PI * 2 / nodes.Count;
            points[ManifestOptimizer.NormalizeLocation(nodes[i])] = new PointF(
                center.X + (float)Math.Cos(angle) * radiusX,
                center.Y + (float)Math.Sin(angle) * radiusY);
        }

        using var routePen = new Pen(Color.FromArgb(37, 109, 164), 3) { EndCap = LineCap.ArrowAnchor };
        using var defaultPen = new Pen(Color.FromArgb(130, 148, 164), 2) { DashStyle = DashStyle.Dash, EndCap = LineCap.ArrowAnchor };
        for (var i = 1; i < route.Count; i++)
        {
            var from = points[ManifestOptimizer.NormalizeLocation(route[i - 1])];
            var to = points[ManifestOptimizer.NormalizeLocation(route[i])];
            var estimate = _database.Estimate(route[i - 1], route[i]);
            e.Graphics.DrawLine(estimate.IsRecorded ? routePen : defaultPen, from, to);
            var midpoint = new Point((int)((from.X + to.X) / 2), (int)((from.Y + to.Y) / 2));
            TextRenderer.DrawText(e.Graphics, $"{estimate.Minutes} min", Font, midpoint, Color.FromArgb(25, 52, 74),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            var point = points[ManifestOptimizer.NormalizeLocation(nodes[i])];
            var circle = new RectangleF(point.X - 19, point.Y - 19, 38, 38);
            using var brush = new SolidBrush(i == 0 ? Color.FromArgb(23, 128, 93) : Color.FromArgb(34, 111, 168));
            e.Graphics.FillEllipse(brush, circle);
            TextRenderer.DrawText(e.Graphics, (i + 1).ToString(), new Font(Font, FontStyle.Bold), Rectangle.Round(circle), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var label = ManifestOptimizer.MapLocationLabel(nodes[i]);
            var labelRect = new Rectangle((int)point.X - 125, (int)point.Y + 23, 250, 42);
            TextRenderer.DrawText(e.Graphics, label, Font, labelRect, Color.FromArgb(24, 38, 55),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
        }

        TextRenderer.DrawText(e.Graphics, "Solid = recorded time    Dashed = default estimate", Font,
            new Rectangle(12, 10, ClientSize.Width - 24, 24), Color.FromArgb(72, 88, 104), TextFormatFlags.HorizontalCenter);
    }
}
