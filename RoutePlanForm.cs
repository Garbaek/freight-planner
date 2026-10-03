namespace FreightCopilot;

public sealed class RoutePlanForm : Form
{
    private readonly FlowLayoutPanel _timeline = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(18, 12, 18, 24),
        BackColor = Color.FromArgb(242, 245, 248)
    };

    public RoutePlanForm(ManifestResult manifest, ShipProfile ship, string formattedManifest,
        TravelTimeDatabase travelTimes)
    {
        var route = manifest.LocalRoute;
        Text = "Freight Planner — Flight Route";
        Width = 1040;
        Height = 850;
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = _timeline.BackColor;
        Font = new Font("Segoe UI", 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 122, BackColor = Color.FromArgb(20, 31, 46), Padding = new Padding(22, 16, 22, 12) };
        var title = new Label
        {
            Text = manifest.HasUnverifiedValues ? "MANUAL OVERRIDE ROUTE" : manifest.IsManualSelection ? "CHECKED CONTRACT ROUTE" : "NEXT FREIGHT RUN",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 18),
            AutoSize = true,
            Location = new Point(22, 14)
        };
        var summary = new Label
        {
            Text = $"{ship.Name}  •  {manifest.TotalScu:N0}/{ship.CapacityScu:N0} SCU  •  " +
                   (manifest.UnknownRewardCount > 0 ? $"{manifest.TotalReward:N0} known aUEC + {manifest.UnknownRewardCount} unread  •  " : $"{manifest.TotalReward:N0} aUEC  •  ") +
                   $"~{manifest.EstimatedTripMinutes} min + {manifest.SafetyMarginMinutes} min reserve  •  " +
                   (manifest.UnknownAvailabilityCount > 0 ? $"{manifest.UnknownAvailabilityCount} timer(s) unread" :
                    manifest.HasKnownAvailability ? $"{manifest.HeadroomMinutes} min headroom" : "accepted contracts — no offer timer"),
            ForeColor = Color.FromArgb(188, 204, 220),
            AutoSize = true,
            Location = new Point(24, 55)
        };
        var hint = new Label
        {
            Text = manifest.HasUnverifiedValues
                ? "Manual override: verify unread reward/time values in game. Then follow the numbered stops."
                : "Follow the numbered stops. At combined stops: unload first, then collect and repack.",
            ForeColor = manifest.HasUnverifiedValues ? Color.FromArgb(255, 196, 105) : Color.FromArgb(124, 211, 177),
            AutoSize = true,
            Location = new Point(24, 82)
        };
        header.Controls.AddRange([title, summary, hint]);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = Color.White };
        var close = new Button { Text = "Close", AutoSize = true };
        var copy = new Button { Text = "Copy route", AutoSize = true };
        close.Click += (_, _) => Close();
        copy.Click += (_, _) => Clipboard.SetText(formattedManifest);
        footer.Controls.AddRange([close, copy]);

        AddContractChecklist(manifest);
        foreach (var step in ManifestOptimizer.BuildRouteGuide(route, manifest, travelTimes)) AddStep(step);
        Controls.Add(_timeline);
        Controls.Add(footer);
        Controls.Add(header);
        Resize += (_, _) => ResizeRows();
    }

    private void AddContractChecklist(ManifestResult manifest)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 18),
            BackColor = _timeline.BackColor
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var markerPanel = new Panel { Dock = DockStyle.Fill, MinimumSize = new Size(64, 80), BackColor = _timeline.BackColor };
        markerPanel.Controls.Add(new Label
        {
            Text = "✓",
            Font = new Font("Segoe UI Semibold", 14),
            ForeColor = Color.White,
            BackColor = manifest.HasUnverifiedValues ? Color.FromArgb(196, 112, 36) : Color.FromArgb(23, 128, 93),
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(40, 40),
            Location = new Point(10, 8)
        });

        var card = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(18, 13, 18, 14),
            Margin = new Padding(0),
            BackColor = manifest.HasUnverifiedValues ? Color.FromArgb(255, 247, 229) : Color.FromArgb(235, 250, 244),
            BorderStyle = BorderStyle.FixedSingle
        };
        card.Controls.Add(new Label
        {
            Text = $"ACCEPT THESE CONTRACTS IN ORDER (1 → {manifest.Contracts.Count})",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 14),
            ForeColor = manifest.HasUnverifiedValues ? Color.FromArgb(126, 70, 18) : Color.FromArgb(20, 91, 68),
            Margin = new Padding(0, 0, 0, 8)
        });
        foreach (var contract in ManifestOptimizer.BuildContractChecklist(manifest))
        {
            card.Controls.Add(new Label
            {
                Text = contract,
                AutoSize = true,
                Font = new Font("Segoe UI", 10),
                ForeColor = Color.FromArgb(40, 63, 54),
                Margin = new Padding(0, 2, 0, 4),
                MaximumSize = new Size(820, 0)
            });
        }

        row.Controls.Add(markerPanel, 0, 0);
        row.Controls.Add(card, 1, 0);
        row.Tag = card;
        _timeline.Controls.Add(row);
        ResizeRow(row);
    }

    private void AddStep(RouteGuideStep step)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 12),
            BackColor = _timeline.BackColor
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var markerPanel = new Panel { Dock = DockStyle.Fill, MinimumSize = new Size(64, 80), BackColor = _timeline.BackColor };
        var marker = new Label
        {
            Text = step.Number.ToString(),
            Font = new Font("Segoe UI Semibold", 12),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(34, 111, 168),
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(40, 40),
            Location = new Point(10, 8)
        };
        markerPanel.Controls.Add(marker);

        var card = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(18, 13, 18, 14),
            Margin = new Padding(0),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        card.Controls.Add(new Label
        {
            Text = step.Location,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 15),
            ForeColor = Color.FromArgb(24, 38, 55),
            Margin = new Padding(0, 0, 0, 8)
        });
        if (step.Number > 1)
        {
            card.Controls.Add(new Label
            {
                Text = $"Estimated flight from previous stop: {step.TravelMinutesFromPrevious} min ({(step.TravelTimeIsRecorded ? "recorded" : "default estimate")})",
                AutoSize = true,
                Font = new Font("Segoe UI", 9),
                ForeColor = step.TravelTimeIsRecorded ? Color.FromArgb(23, 128, 93) : Color.FromArgb(118, 92, 45),
                Margin = new Padding(0, 0, 0, 7)
            });
        }
        AddLines(card, "DROP OFF", step.Deliveries, Color.FromArgb(183, 71, 53));
        AddLines(card, "PICK UP", step.Pickups, Color.FromArgb(23, 128, 93));
        AddLines(card, "PACK", step.Packing, Color.FromArgb(38, 91, 155));

        row.Controls.Add(markerPanel, 0, 0);
        row.Controls.Add(card, 1, 0);
        row.Tag = card;
        _timeline.Controls.Add(row);
        ResizeRow(row);
    }

    private static void AddLines(FlowLayoutPanel card, string heading, IReadOnlyList<string> lines, Color color)
    {
        if (lines.Count == 0) return;
        card.Controls.Add(new Label
        {
            Text = heading,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 9),
            ForeColor = color,
            Margin = new Padding(0, 4, 0, 2)
        });
        foreach (var line in lines)
        {
            card.Controls.Add(new Label
            {
                Text = $"› {line}",
                AutoSize = true,
                Font = new Font("Segoe UI", 10),
                ForeColor = Color.FromArgb(48, 58, 68),
                Margin = new Padding(0, 1, 0, 2),
                MaximumSize = new Size(820, 0)
            });
        }
    }

    private void ResizeRows()
    {
        foreach (Control control in _timeline.Controls)
            if (control is TableLayoutPanel row) ResizeRow(row);
    }

    private void ResizeRow(TableLayoutPanel row)
    {
        var width = Math.Max(620, _timeline.ClientSize.Width - 58);
        row.Width = width;
        if (row.Tag is FlowLayoutPanel card)
        {
            card.Width = Math.Max(520, width - 64);
            foreach (Control child in card.Controls) child.MaximumSize = new Size(Math.Max(460, card.Width - 42), 0);
        }
    }
}
