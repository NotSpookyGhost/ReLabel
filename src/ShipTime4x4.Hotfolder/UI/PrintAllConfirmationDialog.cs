namespace ShipTime4x4.Hotfolder.UI;

internal sealed class PrintAllConfirmationDialog : Form
{
    public PrintAllConfirmationDialog(long labelCount)
    {
        Text = "Print All";
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        ForeColor = Color.FromArgb(33, 43, 67);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(460, 190);
        Padding = new Padding(24);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        var question = new Label
        {
            Text = $"Do you want to print all {labelCount} {(labelCount == 1 ? "label" : "labels")}?",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 16f),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var print = new Button
        {
            Text = "Print All", DialogResult = DialogResult.OK,
            Size = new Size(116, 40), FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(67, 92, 245), ForeColor = Color.White,
            Cursor = Cursors.Hand, Margin = new Padding(8, 0, 0, 0)
        };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Size = new Size(96, 40), FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(239, 243, 252), ForeColor = ForeColor,
            Cursor = Cursors.Hand, Margin = Padding.Empty
        };
        print.FlatAppearance.BorderSize = 0;
        cancel.FlatAppearance.BorderSize = 0;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 40,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Margin = Padding.Empty
        };
        buttons.Controls.AddRange([print, cancel]);
        Controls.Add(question);
        Controls.Add(buttons);
        AcceptButton = print;
        CancelButton = cancel;
    }
}
