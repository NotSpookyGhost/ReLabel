using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ShipTime4x4.Hotfolder.Printing;

public interface IRawPrinterClient
{
    void Print(string printerName, string documentName, ReadOnlySpan<byte> data);
}

public enum PrinterAvailability { Unknown, Ready, Busy, Warning, Error }
public sealed record PrinterStatusSnapshot(PrinterAvailability Availability, string Message, int QueueDepth = 0);
public interface IPrinterStatusProvider
{
    PrinterStatusSnapshot GetStatus(string printerName);
}

public sealed class RawPrinterClient : IRawPrinterClient, IPrinterStatusProvider
{
    public void Print(string printerName, string documentName, ReadOnlySpan<byte> data)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            throw new InvalidOperationException("Select a physical Zebra printer queue first.");
        if (!OpenPrinter(printerName, out var handle, nint.Zero))
            ThrowWin32("Unable to open the selected printer queue");
        try
        {
            var info = new DocInfo { DocumentName = documentName, DataType = "RAW" };
            if (StartDocPrinter(handle, 1, ref info) == 0)
                ThrowWin32("Unable to start the print job");
            try
            {
                if (!StartPagePrinter(handle))
                    ThrowWin32("Unable to start the label page");
                try
                {
                    var buffer = data.ToArray();
                    if (!WritePrinter(handle, buffer, buffer.Length, out var written) || written != buffer.Length)
                        ThrowWin32("The Zebra queue did not accept the complete label");
                }
                finally { EndPagePrinter(handle); }
            }
            finally { EndDocPrinter(handle); }
        }
        finally { ClosePrinter(handle); }
    }

    public PrinterStatusSnapshot GetStatus(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return new PrinterStatusSnapshot(PrinterAvailability.Unknown, "Printer not selected");
        if (!OpenPrinter(printerName, out var handle, nint.Zero))
            return new PrinterStatusSnapshot(PrinterAvailability.Error,
                $"Printer unavailable ({new Win32Exception(Marshal.GetLastWin32Error()).Message})");
        try
        {
            GetPrinter(handle, 2, nint.Zero, 0, out var needed);
            if (needed == 0) return new PrinterStatusSnapshot(PrinterAvailability.Unknown, "Printer status unavailable");
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetPrinter(handle, 2, buffer, needed, out _))
                    return new PrinterStatusSnapshot(PrinterAvailability.Unknown, "Printer status unavailable");
                var info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
                return InterpretStatus(info.Status, (int)info.Jobs);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { ClosePrinter(handle); }
    }

    private static PrinterStatusSnapshot InterpretStatus(uint status, int jobs)
    {
        if ((status & 0x00000010) != 0) return new(PrinterAvailability.Error, "Paper out", jobs);
        if ((status & 0x00000008) != 0) return new(PrinterAvailability.Error, "Paper jam", jobs);
        if ((status & 0x00400000) != 0) return new(PrinterAvailability.Error, "Printer door open", jobs);
        if ((status & 0x00000080) != 0) return new(PrinterAvailability.Error, "Printer offline", jobs);
        if ((status & 0x00100000) != 0) return new(PrinterAvailability.Error, "Needs attention", jobs);
        if ((status & 0x00000001) != 0) return new(PrinterAvailability.Warning, "Printer paused", jobs);
        if ((status & 0x00000040) != 0) return new(PrinterAvailability.Warning, "Paper problem", jobs);
        if ((status & 0x00000200) != 0 || (status & 0x00000400) != 0 || jobs > 0)
            return new(PrinterAvailability.Busy, jobs > 0 ? $"Printing - {jobs} queued" : "Printing", jobs);
        return new(PrinterAvailability.Ready, "Printer ready", jobs);
    }

    private static void ThrowWin32(string message) =>
        throw new InvalidOperationException($"{message}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public nint ServerName, PrinterName, ShareName, PortName, DriverName, Comment, Location;
        public nint DevMode, SeparatorFile, PrintProcessor, DataType, Parameters, SecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePagesPerMinute;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out nint printerHandle, nint defaults);
    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetPrinter(nint printerHandle, uint level, nint printer, uint size, out uint needed);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool ClosePrinter(nint printerHandle);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(nint printerHandle, int level, ref DocInfo info);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndDocPrinter(nint printerHandle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool StartPagePrinter(nint printerHandle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndPagePrinter(nint printerHandle);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(nint printerHandle, byte[] buffer, int count, out int written);
}
