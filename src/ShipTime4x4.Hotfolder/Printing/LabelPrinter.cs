using ShipTime4x4.Core.Printing;
using ShipTime4x4.Core.Processing;
using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.Printing;

public interface IDriverPrinterClient
{
    void Print(string printerName, string documentName, IReadOnlyList<MonochromePage> pages, int dpi);
}

public sealed class LabelPrinter(IRawPrinterClient raw, IDriverPrinterClient? driver = null)
{
    private readonly IDriverPrinterClient _driver = driver ?? new WindowsDriverPrinterClient();

    public void Print(HotfolderConfiguration settings, string documentName, PreparedLabel prepared)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(settings.PhysicalPrinterQueue))
            throw new InvalidOperationException("Select a printer in Settings first.");
        switch (settings.PrinterOutput)
        {
            case PrinterOutputMode.Zpl:
                raw.Print(settings.PhysicalPrinterQueue, documentName, prepared.Zpl);
                break;
            case PrinterOutputMode.WindowsDriver:
                _driver.Print(settings.PhysicalPrinterQueue, documentName, prepared.GetPrintPages(),
                    settings.PrintResolutionDpi);
                break;
            case PrinterOutputMode.Epl2:
            case PrinterOutputMode.Tspl:
                var epl = settings.PrinterOutput == PrinterOutputMode.Epl2;
                var data = NativeRasterEncoder.Encode(settings.PrinterOutput, prepared.GetPrintPages(),
                    new NativePrinterOptions(settings.PrintResolutionDpi, settings.NativeMedia,
                        settings.NativeGapMillimeters,
                        settings.NativeUsePrinterDefaults ? null : epl ? settings.EplDensity : settings.TsplDensity,
                        settings.NativeUsePrinterDefaults ? null : epl ? settings.EplSpeed : settings.TsplSpeedIps));
                raw.Print(settings.PhysicalPrinterQueue, documentName, data);
                break;
            default:
                throw new InvalidOperationException("Unsupported printer output mode.");
        }
    }
}
