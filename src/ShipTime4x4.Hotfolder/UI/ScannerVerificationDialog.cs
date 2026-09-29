namespace ShipTime4x4.Hotfolder.UI;

public sealed class ScannerVerificationDialog : Form
{
    private readonly TextBox _scan = new();

    public ScannerVerificationDialog(string fileName)
    {
        Text = "Verify printed barcode";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 244);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        Controls.Add(new Label
        {
            Text = "Scan the printed label", Location = new Point(26, 22), AutoSize = true,
            Font = new Font("Segoe UI Semibold", 17f), ForeColor = Color.FromArgb(33, 43, 67)
        });
        Controls.Add(new Label
        {
            Text = $"Use the USB scanner on {fileName}. ReLabel compares the scan locally and never stores its value.",
            Location = new Point(28, 64), Size = new Size(500, 44), ForeColor = Color.FromArgb(92, 104, 119)
        });
        _scan.SetBounds(28, 116, 500, 34);
        _scan.Font = new Font("Segoe UI", 12f);
        Controls.Add(_scan);

        var skip = new Button { Text = "Skip", DialogResult = DialogResult.Cancel, Size = new Size(92, 38),
            Location = new Point(336, 180), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(239, 243, 252) };
        skip.FlatAppearance.BorderSize = 0;
        var verify = new Button { Text = "Verify scan", DialogResult = DialogResult.OK, Size = new Size(100, 38),
            Location = new Point(436, 180), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(67, 92, 245),
            ForeColor = Color.White };
        verify.FlatAppearance.BorderSize = 0;
        Controls.AddRange([skip, verify]);
        AcceptButton = verify;
        CancelButton = skip;
        Shown += (_, _) => _scan.Focus();
        FormClosing += (_, arguments) =>
        {
            if (DialogResult == DialogResult.OK && string.IsNullOrWhiteSpace(_scan.Text))
            {
                arguments.Cancel = true;
                System.Media.SystemSounds.Beep.Play();
                _scan.Focus();
            }
        };
    }

    public string ScannedValue => _scan.Text.Trim();
}
