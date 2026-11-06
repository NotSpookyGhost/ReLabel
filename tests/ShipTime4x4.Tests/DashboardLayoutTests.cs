using System.Drawing;
using System.Windows.Forms;
using ShipTime4x4.Hotfolder.Services;
using ShipTime4x4.Hotfolder.UI;

namespace ShipTime4x4.Tests;

public sealed class DashboardLayoutTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task PrintAllToolbarDoesNotOverlapTabsAtMinimumSize(float scale)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ReLabelLayoutTests", Guid.NewGuid().ToString("N"));
            try
            {
                using (var coordinator = new HotfolderCoordinator(root, AppContext.BaseDirectory))
                using (var form = new MainForm(coordinator))
                {
                    form.Scale(new SizeF(scale, scale));
                    form.Size = new Size((int)(1080 * scale), (int)(700 * scale));
                    form.CreateControl();
                    form.PerformLayout();
                    var printAll = Descendants(form).OfType<Button>()
                        .Single(button => button.AccessibleName == "Print all Ready labels");
                    var toolbar = printAll.Parent!;
                    toolbar.Dock = DockStyle.None;
                    // Exercise both single-row and wrapped toolbar arrangements.
                    foreach (var width in new[] { (int)(700 * scale), (int)(490 * scale) })
                    {
                        toolbar.Width = width;
                        toolbar.PerformLayout();
                        Assert.Equal(width, toolbar.Width);
                        var buttons = toolbar.Controls.OfType<Button>().ToArray();
                        Assert.Equal(4, buttons.Length);
                        foreach (var button in buttons)
                        {
                            Assert.True(button.Left >= 0 && button.Right <= toolbar.ClientSize.Width,
                                $"{button.Text} falls outside the toolbar at scale {scale}");
                            Assert.True(button.Top >= 0 && button.Bottom <= toolbar.ClientSize.Height);
                            Assert.All(buttons.Where(other => other != button),
                                other => Assert.False(button.Bounds.IntersectsWith(other.Bounds),
                                    $"{button.Text} overlaps {other.Text} at scale {scale}"));
                        }
                    }
                    Assert.False(printAll.Enabled);
                    Assert.True(form.AllowDrop);
                    Assert.All(Descendants(form), control => Assert.True(control.AllowDrop));
                    Assert.DoesNotContain(Descendants(form).OfType<Button>(),
                        button => button.Text.Contains("Add PDF"));
                    using var dynamicCard = new Panel();
                    using var dynamicChild = new Label();
                    dynamicCard.Controls.Add(dynamicChild);
                    form.Controls.Add(dynamicCard);
                    Assert.True(dynamicCard.AllowDrop);
                    Assert.True(dynamicChild.AllowDrop);
                    var overlay = Descendants(form).OfType<PdfDropOverlay>().Single();
                    Assert.False(overlay.IsActive);
                    AssertDragEffect(dynamicChild,
                        new DataObject(DataFormats.FileDrop, new[] { "label.PDF", "notes.txt" }),
                        DragDropEffects.Copy | DragDropEffects.Move, DragDropEffects.Copy);
                    Assert.True(overlay.IsActive);
                    Assert.Contains("Drop 1 PDF", overlay.AccessibleDescription);
                    Assert.Equal(form.ClientRectangle, overlay.Bounds);
                    Assert.Equal(0, form.Controls.GetChildIndex(overlay));
                    RaiseDragLeave(dynamicChild);
                    // Entering the overlay cancels the old control's pending leave.
                    AssertDragEffect(overlay,
                        new DataObject(DataFormats.FileDrop, new[] { "one.pdf", "two.pdf" }),
                        DragDropEffects.Copy, DragDropEffects.Copy);
                    PumpMessagesFor(220);
                    Assert.True(overlay.IsActive);
                    Assert.Contains("Drop 2 PDF", overlay.AccessibleDescription);
                    RaiseDragLeave(overlay);
                    PumpMessagesFor(220);
                    Assert.False(overlay.IsActive);
                    AssertDragEffect(dynamicChild,
                        new DataObject(DataFormats.FileDrop, new[] { "label.pdf" }),
                        DragDropEffects.Move, DragDropEffects.None);
                    Assert.False(overlay.IsActive);
                    AssertDragEffect(dynamicChild,
                        new DataObject(DataFormats.FileDrop, new[] { "notes.txt" }),
                        DragDropEffects.Copy, DragDropEffects.None);
                    AssertDragEffect(dynamicChild, new DataObject(DataFormats.Text, "label.pdf"),
                        DragDropEffects.Copy, DragDropEffects.None);
                    Assert.False(overlay.IsActive);
                    AssertDragEffect(overlay,
                        new DataObject(DataFormats.FileDrop, new[] { "one.pdf" }),
                        DragDropEffects.Copy, DragDropEffects.Copy);
                    // Even an unsupported drop must clear the overlay immediately.
                    var drop = new DragEventArgs(new DataObject(), 0, 0, 0,
                        DragDropEffects.Copy, DragDropEffects.Copy);
                    typeof(Control).GetMethod("OnDragDrop",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(overlay, [drop]);
                    Assert.False(overlay.IsActive);
                }
                completion.SetResult(true);
            }
            catch (Exception exception) { completion.SetException(exception); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void AssertDragEffect(Control target, IDataObject data,
        DragDropEffects allowed, DragDropEffects expected)
    {
        var arguments = new DragEventArgs(data, 0, 0, 0, allowed, DragDropEffects.None);
        typeof(Control).GetMethod("OnDragEnter",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(target, [arguments]);
        Assert.Equal(expected, arguments.Effect);
    }

    private static void RaiseDragLeave(Control target) =>
        typeof(Control).GetMethod("OnDragLeave",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(target, [EventArgs.Empty]);

    private static void PumpMessagesFor(int milliseconds)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
