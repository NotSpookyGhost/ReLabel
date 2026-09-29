using ShipTime4x4.Hotfolder.Services;
using ShipTime4x4.Hotfolder.UI;

namespace ShipTime4x4.Hotfolder;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments.Length == 5 &&
            string.Equals(arguments[0], "--initialize-folders", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var installerDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ShipTime4x4Zebra", "Hotfolder");
                InstallerConfigurationInitializer.InitializeIfMissing(installerDataFolder,
                    arguments[1], arguments[2], arguments[3], arguments[4]);
                return 0;
            }
            catch
            {
                return 1;
            }
        }
        using var singleInstance = new Mutex(true, "Local\\ReLabelHotfolder", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("ReLabel is already running.", "ReLabel",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }

        ApplicationConfiguration.Initialize();
        var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShipTime4x4Zebra", "Hotfolder");
        using var coordinator = new HotfolderCoordinator(dataFolder, AppContext.BaseDirectory);
        using var form = new MainForm(coordinator);
        coordinator.Start();
        Application.Run(form);
        return 0;
    }

}
