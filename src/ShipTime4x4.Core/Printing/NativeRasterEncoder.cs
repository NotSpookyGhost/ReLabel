using System.Globalization;
using System.Text;

namespace ShipTime4x4.Core.Printing;

public sealed record NativePrinterOptions(int Dpi = 203, LabelMediaMode Media = LabelMediaMode.Gap,
    double GapMillimeters = 3, int? Density = null, double? Speed = null);

/// <summary>Binary-safe EPL2 GW and TSPL BITMAP output; never embeds PDF text in commands.</summary>
public static class NativeRasterEncoder
{
    public static byte[] Encode(PrinterOutputMode mode, IReadOnlyList<MonochromePage> pages,
        NativePrinterOptions options)
    {
        if (mode is not (PrinterOutputMode.Epl2 or PrinterOutputMode.Tspl))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (options.Dpi is not (203 or 300 or 600) ||
            (mode == PrinterOutputMode.Epl2 && options.Dpi == 600) || !Enum.IsDefined(options.Media) ||
            !double.IsFinite(options.GapMillimeters) || options.GapMillimeters is < 0.1 or > 25 ||
            options.Density is < 0 or > 15 ||
            (options.Speed.HasValue && (!double.IsFinite(options.Speed.Value) || options.Speed is < 1 or > 14)) ||
            (mode == PrinterOutputMode.Epl2 && options.Speed.HasValue &&
             (options.Speed > 6 || options.Speed != Math.Truncate(options.Speed.Value))))
            throw new ArgumentException("Unsupported native printer settings.");
        if (pages.Count is < 1 or > 500 ||
            pages.Sum(page => (long)page.Data.Length + 1024) > MonochromePage.MaximumJobBytes)
            throw new InvalidDataException("Native print job exceeds safety limits.");
        using var output = new MemoryStream();
        void Command(FormattableString command) =>
            output.Write(Encoding.ASCII.GetBytes(command.ToString(CultureInfo.InvariantCulture)));
        foreach (var page in pages)
        {
            if (mode == PrinterOutputMode.Epl2)
            {
                // Q/q clear the printer's image buffer, so they must precede GW.
                Command($"\nN\nq{page.Width}\n");
                var gap = Math.Max(1, (int)Math.Round(options.GapMillimeters * options.Dpi / 25.4));
                if (options.Media == LabelMediaMode.BlackMark)
                    Command($"Q{page.Height},B{gap}\n");
                else
                    Command($"Q{page.Height},{(options.Media == LabelMediaMode.Continuous ? 0 : gap)}\n");
                Command($"R0,0\nZT\n");
                if (options.Density.HasValue) Command($"D{options.Density.Value}\n");
                if (options.Speed.HasValue) Command($"S{options.Speed.Value:0}\n");
                Command($"GW0,0,{page.BytesPerRow},{page.Height},");
            }
            else
            {
                Command($"SIZE {page.Width * 25.4 / options.Dpi:0.###} mm,{page.Height * 25.4 / options.Dpi:0.###} mm\r\n");
                if (options.Media == LabelMediaMode.BlackMark)
                    Command($"BLINE {options.GapMillimeters:0.###} mm,0 mm\r\n");
                else
                    Command($"GAP {(options.Media == LabelMediaMode.Continuous ? 0 : options.GapMillimeters):0.###} mm,0 mm\r\n");
                Command($"DIRECTION 1,0\r\nREFERENCE 0,0\r\nOFFSET 0 mm\r\n");
                if (options.Density.HasValue) Command($"DENSITY {options.Density.Value}\r\n");
                if (options.Speed.HasValue) Command($"SPEED {options.Speed.Value:0.#}\r\n");
                Command($"CLS\r\nBITMAP 0,0,{page.BytesPerRow},{page.Height},0,");
            }
            // Both protocols use zero for a black dot, unlike ZPL and our internal bitmap.
            // Write actual bytes (including NUL / CR / LF); text encodings would corrupt barcodes.
            foreach (var value in page.Data) output.WriteByte((byte)~value);
            if (mode == PrinterOutputMode.Epl2) Command($"\nP1\n");
            else Command($"\r\nPRINT 1,1\r\n");
        }
        return output.ToArray();
    }
}
