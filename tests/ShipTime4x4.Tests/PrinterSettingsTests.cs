using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ShipTime4x4.Core.Printing;
using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.UI;

namespace ShipTime4x4.Tests;

public sealed class PrinterSettingsTests
{
    [Theory]
    [InlineData(PrinterOutputMode.Zpl)]
    [InlineData(PrinterOutputMode.WindowsDriver)]
    [InlineData(PrinterOutputMode.Epl2)]
    [InlineData(PrinterOutputMode.Tspl)]
    public Task PrinterModeControlsRoundTripWithoutChangingOtherSettings(PrinterOutputMode mode) => OnSta(() =>
    {
        var original = new HotfolderConfiguration
        {
            PrinterOutput = mode, PhysicalPrinterQueue = "ReLabel test queue",
            EplDensity = 11, EplSpeed = 3, TsplDensity = 14, TsplSpeedIps = 2.5,
            NativeGapMillimeters = 2.5, NativeMedia = LabelMediaMode.BlackMark,
            PrintDarkness = 20, PrintSpeedIps = 3, ConfirmPrintAll = true
        };
        using var dialog = new SettingsDialog(original, "unused-audit.jsonl", _ => { });
        dialog.CreateControl();
        dialog.PerformLayout();
        var grid = Field<TableLayoutPanel>(dialog, "_printerGrid");
        Assert.Equal(mode == PrinterOutputMode.Zpl, grid.RowStyles[4].Height > 0);
        Assert.Equal(mode == PrinterOutputMode.Epl2, grid.RowStyles[7].Height > 0);
        Assert.Equal(mode == PrinterOutputMode.Tspl, grid.RowStyles[9].Height > 0);
        Assert.False(Field<NumericUpDown>(dialog, "_eplDensity").Enabled);
        Field<CheckBox>(dialog, "_nativeDefaults").Checked = false;
        Assert.True(Field<NumericUpDown>(dialog, "_eplDensity").Enabled);
        var saved = (HotfolderConfiguration)typeof(SettingsDialog)
            .GetMethod("BuildConfiguration", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null)!;
        saved.Validate();
        Assert.Equal(mode, saved.PrinterOutput);
        Assert.Equal(original.EplDensity, saved.EplDensity);
        Assert.Equal(original.EplSpeed, saved.EplSpeed);
        Assert.Equal(original.TsplDensity, saved.TsplDensity);
        Assert.Equal(original.TsplSpeedIps, saved.TsplSpeedIps);
        Assert.Equal(original.PrintDarkness, saved.PrintDarkness);
        Assert.Equal(original.PrintSpeedIps, saved.PrintSpeedIps);
        Assert.Equal(original.NativeGapMillimeters, saved.NativeGapMillimeters);
        Assert.Equal(original.NativeMedia, saved.NativeMedia);
        Assert.Equal(original.IncomingFolder, saved.IncomingFolder);
        Assert.True(saved.ConfirmPrintAll);
        Assert.IsType<Panel>(grid.Parent);
        Assert.True(((Panel)grid.Parent!).AutoScroll);

        var output = Environment.GetEnvironmentVariable("RELABEL_UI_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            dialog.ShowInTaskbar = false;
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-30000, -30000);
            dialog.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
            bitmap.Save(Path.Combine(output, $"printer-settings-{mode}.png"));
            dialog.Hide();
        }
    });

    [Fact]
    public Task SwitchingModesRevealsOnlyApplicableControls() => OnSta(() =>
    {
        using var dialog = new SettingsDialog(new HotfolderConfiguration(), "unused", _ => { });
        var modes = Field<ComboBox>(dialog, "_output");
        var grid = Field<TableLayoutPanel>(dialog, "_printerGrid");
        foreach (var index in new[] { 1, 2, 3, 0 })
        {
            modes.SelectedIndex = index;
            Assert.Equal(index == 0, grid.RowStyles[4].Height > 0);
            Assert.Equal(index == 2, grid.RowStyles[7].Height > 0);
            Assert.Equal(index == 3, grid.RowStyles[9].Height > 0);
        }
    });

    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static async Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
