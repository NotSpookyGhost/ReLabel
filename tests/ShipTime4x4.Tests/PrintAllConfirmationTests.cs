using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.Services;
using ShipTime4x4.Hotfolder.UI;

namespace ShipTime4x4.Tests;

public sealed class PrintAllConfirmationTests
{
    [Fact]
    public void ConfirmationDefaultsOffForNewAndExistingConfigurations()
    {
        Assert.False(new HotfolderConfiguration().ConfirmPrintAll);
        Assert.False(JsonSerializer.Deserialize<HotfolderConfiguration>("{}")!.ConfirmPrintAll);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmationChoiceSurvivesSaveAndReload(bool enabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "ReLabelConfirmationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            store.Save(new HotfolderConfiguration { ConfirmPrintAll = enabled });
            Assert.Equal(enabled, new ConfigurationStore(root).Load().ConfirmPrintAll);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CountIncludesEveryPageRatherThanOnlyPdfFiles()
    {
        Assert.Equal(6L, PrintBatch.LabelCount([
            new() { PageCount = 1 }, new() { PageCount = 2 }, new() { PageCount = 3 }]));
        Assert.Equal(2L, PrintBatch.LabelCount([new() { PageCount = 0 }, new() { PageCount = -1 }]));
        Assert.Equal(2L * int.MaxValue, PrintBatch.LabelCount([
            new() { PageCount = int.MaxValue }, new() { PageCount = int.MaxValue }]));
    }

    [Theory]
    [InlineData(1f, 1L)]
    [InlineData(1.5f, 7L)]
    [InlineData(2f, 500L)]
    public Task ConfirmationUsesThemedButtonsAndFitsAtDisplayScales(float scale, long count) => OnSta(() =>
    {
        using var dialog = new PrintAllConfirmationDialog(count);
        dialog.Scale(new SizeF(scale, scale));
        dialog.CreateControl();
        dialog.PerformLayout();
        var question = dialog.Controls.OfType<Label>().Single();
        Assert.Equal($"Do you want to print all {count} {(count == 1 ? "label" : "labels")}?", question.Text);
        var print = Assert.IsType<Button>(dialog.AcceptButton);
        var cancel = Assert.IsType<Button>(dialog.CancelButton);
        Assert.Equal(DialogResult.OK, print.DialogResult);
        Assert.Equal(DialogResult.Cancel, cancel.DialogResult);
        Assert.Equal(Color.FromArgb(67, 92, 245), print.BackColor);
        Assert.Equal(Color.White, dialog.BackColor);
        Assert.Equal(FlatStyle.Flat, print.FlatStyle);
        Assert.Equal(0, print.FlatAppearance.BorderSize);
        Assert.False(print.Bounds.IntersectsWith(cancel.Bounds));
        foreach (var button in new[] { print, cancel })
        {
            Assert.True(button.Left >= 0 && button.Right <= button.Parent!.ClientSize.Width);
            Assert.True(button.Top >= 0 && button.Bottom <= button.Parent!.ClientSize.Height);
        }
        Assert.True(question.Bottom <= print.Parent!.Top);
        Assert.True(TextRenderer.MeasureText(question.Text, question.Font,
            new Size(question.Width, int.MaxValue), TextFormatFlags.WordBreak).Height <= question.Height);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AppSettingsLoadsAndSavesTheConfirmationToggle(bool initial) => OnSta(() =>
    {
        var configuration = new HotfolderConfiguration { ConfirmPrintAll = initial };
        using var dialog = new SettingsDialog(configuration, "unused-audit.jsonl", _ => { });
        var tabs = dialog.Controls.OfType<TabControl>().Single();
        var page = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "App Settings");
        var options = page.Controls.OfType<FlowLayoutPanel>().Single();
        var toggle = options.Controls.OfType<CheckBox>()
            .Single(check => check.AccessibleName == "Print All confirmation");
        Assert.Equal(initial, toggle.Checked);
        toggle.Checked = !initial;
        var saved = (HotfolderConfiguration)typeof(SettingsDialog)
            .GetMethod("BuildConfiguration", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null)!;
        Assert.Equal(!initial, saved.ConfirmPrintAll);
        Assert.Equal(configuration.IncomingFolder, saved.IncomingFolder);
        Assert.Equal(configuration.ScannerVerificationEnabled, saved.ScannerVerificationEnabled);
        Assert.Equal(configuration.AutoPrint, saved.AutoPrint);
    });

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
