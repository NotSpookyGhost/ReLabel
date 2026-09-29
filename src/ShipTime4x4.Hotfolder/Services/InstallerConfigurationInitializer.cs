using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.Services;

public static class InstallerConfigurationInitializer
{
    public static bool InitializeIfMissing(string dataFolder, string incomingFolder, string readyFolder,
        string archiveFolder, string printedFolder)
    {
        var store = new ConfigurationStore(dataFolder);
        if (File.Exists(store.Path))
            return false;

        var configuration = new HotfolderConfiguration
        {
            IncomingFolder = incomingFolder,
            ReadyFolder = readyFolder,
            ArchiveFolder = archiveFolder,
            PrintedFolder = printedFolder
        };
        configuration.Validate();
        Directory.CreateDirectory(configuration.IncomingFolder);
        Directory.CreateDirectory(configuration.ReadyFolder);
        Directory.CreateDirectory(configuration.ArchiveFolder);
        Directory.CreateDirectory(configuration.PrintedFolder);

        // Recheck immediately before saving so an existing/user-created configuration always wins.
        if (File.Exists(store.Path))
            return false;
        store.Save(configuration);
        return true;
    }
}
