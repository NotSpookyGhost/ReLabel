using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ShipTime4x4.Hotfolder.UI;

namespace ShipTime4x4.Tests;

public sealed class EmptyLabelStateTests
{
    [Theory]
    [InlineData(420, 300)]
    [InlineData(650, 450)]
    [InlineData(1000, 700)]
    public async Task EmptyStateCentersShortcutAndRaisesImportRequest(int width, int height)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var state = new EmptyLabelState
                {
                    Size = new Size(width, height), BackColor = Color.FromArgb(245, 247, 252)
                };
                state.CreateControl();
                state.PerformLayout();
                var button = state.Controls.OfType<Button>().Single();
                var message = state.Controls.OfType<Label>().Single();
                Assert.Equal("Import PDF files", button.AccessibleName);
                Assert.True(button.TabStop);
                Assert.Equal(button.Width, button.Height);
                Assert.InRange(Math.Abs(button.Left + button.Width / 2 - width / 2), 0, 1);
                Assert.True(button.Bottom < message.Top);
                Assert.True(message.Left >= 0 && message.Right <= width && message.Bottom <= height);
                Assert.Equal("Drag and drop PDF files here\nor add them to the hotfolder.", message.Text);
                var requests = 0;
                state.ImportRequested += (_, _) => requests++;
                typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(button, [EventArgs.Empty]);
                Assert.Equal(1, requests);
                state.ImportEnabled = false;
                Assert.False(button.Enabled);
                state.ImportEnabled = true;
                Assert.True(button.Enabled);
                using var rendered = new Bitmap(width, height);
                state.DrawToBitmap(rendered, state.ClientRectangle);
                if (width == 650 && Environment.GetEnvironmentVariable("RELABEL_CAPTURE_EMPTY_STATE") is { Length: > 0 } capture)
                    rendered.Save(capture, System.Drawing.Imaging.ImageFormat.Png);
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
