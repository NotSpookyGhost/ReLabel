using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.UI;

public sealed class LabelCard : UserControl
{
    private static readonly Color Primary = Color.FromArgb(67, 92, 245);
    private static readonly Color Ink = Color.FromArgb(33, 43, 67);
    private static readonly Color Muted = Color.FromArgb(112, 122, 145);
    private readonly bool _selected;
    private readonly Label _recipient;
    private readonly Label _file;
    private readonly Label _address;
    private readonly Label _date;
    private readonly Label _status;
    private readonly Button _print;
    private readonly Button? _delete;

    public LabelCard(LabelRecord record, bool selected)
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint, true);
        Record = record;
        _selected = selected;
        Height = 106;
        Width = 760;
        Margin = new Padding(0, 0, 0, 10);
        BackColor = Color.White;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;

        var accent = new Panel
        {
            Dock = DockStyle.Left, Width = 5,
            BackColor = selected ? Primary : Color.FromArgb(220, 226, 240)
        };
        _print = ActionButton(record.PrintedAt.HasValue ? "Print again" : "Print", Primary, Color.White, 88);
        _print.Click += (_, _) => PrintRequested?.Invoke(this, EventArgs.Empty);
        if (record.PrintedAt.HasValue)
        {
            _delete = ActionButton("Delete", Color.FromArgb(255, 235, 238), Color.FromArgb(190, 56, 70), 62);
            _delete.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);
        }

        _recipient = new Label
        {
            Text = record.ShipToName, Location = new Point(23, 18),
            Font = new Font("Segoe UI Semibold", 11f), ForeColor = Ink, AutoEllipsis = true
        };
        _file = new Label
        {
            Text = record.PageCount > 1
                ? $"{record.OriginalFileName}  |  {record.PageCount} labels"
                : record.OriginalFileName,
            Location = new Point(23, 54),
            Font = new Font("Segoe UI", 8.8f), ForeColor = Muted, AutoEllipsis = true
        };
        _address = new Label
        {
            Text = string.IsNullOrWhiteSpace(record.ShipToAddress) ? "Address not recognized" : record.ShipToAddress,
            Font = new Font("Segoe UI", 9f), ForeColor = string.IsNullOrWhiteSpace(record.ShipToAddress)
                ? Color.FromArgb(157, 165, 182) : Ink,
            AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft
        };
        _date = new Label
        {
            Text = (record.PrintedAt ?? record.ImportedAt).LocalDateTime.ToString("MMM d, yyyy\nh:mm tt"),
            ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft
        };
        _status = new Label
        {
            Text = StatusText(record), TextAlign = ContentAlignment.MiddleCenter,
            BackColor = StatusBackColor(record), ForeColor = StatusForeColor(record),
            Font = new Font("Segoe UI Semibold", 8.2f)
        };
        if (record.PrintedAt.HasValue && record.ScanVerification is ScanVerificationStatus.Pending or ScanVerificationStatus.Mismatch)
        {
            _status.Cursor = Cursors.Hand;
            _status.Click += (_, _) => VerifyRequested?.Invoke(this, EventArgs.Empty);
        }

        Controls.Add(accent);
        Controls.AddRange([_recipient, _file, _address, _date, _status, _print]);
        if (_delete is not null) Controls.Add(_delete);
        Resize += (_, _) =>
        {
            LayoutFields();
            Invalidate(true);
        };
        foreach (var control in new Control[] { _recipient, _file, _address, _date, _status })
        {
            control.Cursor = Cursors.Hand;
            control.Click += (_, _) => Selected?.Invoke(this, EventArgs.Empty);
            control.DoubleClick += (_, _) => PrintRequested?.Invoke(this, EventArgs.Empty);
        }
        Click += (_, _) => Selected?.Invoke(this, EventArgs.Empty);
        DoubleClick += (_, _) => PrintRequested?.Invoke(this, EventArgs.Empty);
        Paint += PaintBorder;
        LayoutFields();
    }

    public LabelRecord Record { get; }
    public bool IsSelected => _selected;
    public event EventHandler? Selected;
    public event EventHandler? PrintRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? VerifyRequested;

    public void SetPrintProgress(string? message)
    {
        var active = !string.IsNullOrWhiteSpace(message);
        _status.Text = active ? message : StatusText(Record);
        _status.BackColor = active ? Color.FromArgb(230, 246, 255) : StatusBackColor(Record);
        _status.ForeColor = active ? Color.FromArgb(24, 119, 180) : StatusForeColor(Record);
        _print.Enabled = !active;
        LayoutFields();
    }

    private void LayoutFields()
    {
        var right = ClientSize.Width - 18;
        _print.Location = new Point(right - _print.Width, 34);
        right = _print.Left - 9;
        if (_delete is not null)
        {
            _delete.Location = new Point(right - _delete.Width, 34);
            right = _delete.Left - 9;
        }
        var statusWidth = _status.Text.Length > 10 ? 128 : 70;
        _status.SetBounds(right - statusWidth, 40, statusWidth, 26);
        right = _status.Left - 12;
        _date.SetBounds(right - 108, 29, 108, 48);
        right = _date.Left - 14;

        var leftColumnWidth = Math.Clamp((ClientSize.Width - 40) / 4, 165, 195);
        _recipient.SetBounds(23, 17, leftColumnWidth, 27);
        _file.SetBounds(23, 54, leftColumnWidth, 23);
        var addressLeft = 23 + leftColumnWidth + 18;
        _address.SetBounds(addressLeft, 18, Math.Max(80, right - addressLeft), 62);
    }

    private static Button ActionButton(string text, Color background, Color foreground, int width)
    {
        var button = new Button
        {
            Text = text, Size = new Size(width, 38), BackColor = background, ForeColor = foreground,
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 9f), Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void PaintBorder(object? sender, PaintEventArgs arguments)
    {
        using var pen = new Pen(_selected ? Primary : Color.FromArgb(226, 231, 241), _selected ? 2 : 1);
        arguments.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private static string StatusText(LabelRecord record) => record.PrintedAt.HasValue
        ? record.ScanVerification switch
        {
            ScanVerificationStatus.Verified => "Verified",
            ScanVerificationStatus.Mismatch => "Mismatch",
            ScanVerificationStatus.Pending => "Scan needed",
            _ => record.Status == LabelStatus.Warning ? "Note" : "Printed"
        }
        : record.Status switch
        {
            LabelStatus.Printing => "Printing",
            LabelStatus.Warning => "Review",
            LabelStatus.Failed => "Failed",
            _ => "Ready"
        };

    private static Color StatusBackColor(LabelRecord record) => record.PrintedAt.HasValue
        ? record.ScanVerification switch
        {
            ScanVerificationStatus.Mismatch => Color.FromArgb(255, 234, 237),
            ScanVerificationStatus.Pending => Color.FromArgb(255, 245, 221),
            _ => Color.FromArgb(229, 248, 241)
        }
        : record.Status switch
        {
            LabelStatus.Failed => Color.FromArgb(255, 234, 237),
            LabelStatus.Warning => Color.FromArgb(255, 245, 221),
            _ => Color.FromArgb(235, 241, 255)
        };

    private static Color StatusForeColor(LabelRecord record) => record.PrintedAt.HasValue
        ? record.ScanVerification switch
        {
            ScanVerificationStatus.Mismatch => Color.FromArgb(196, 58, 72),
            ScanVerificationStatus.Pending => Color.FromArgb(176, 104, 6),
            _ => Color.FromArgb(34, 145, 104)
        }
        : record.Status switch
        {
            LabelStatus.Failed => Color.FromArgb(196, 58, 72),
            LabelStatus.Warning => Color.FromArgb(176, 104, 6),
            _ => Primary
        };
}
