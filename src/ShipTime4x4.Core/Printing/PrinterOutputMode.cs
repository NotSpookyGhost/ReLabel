namespace ShipTime4x4.Core.Printing;

// Values are persisted. Never reorder: old configurations must retain ZPL.
public enum PrinterOutputMode { Zpl = 0, WindowsDriver = 1, Epl2 = 2, Tspl = 3 }
public enum LabelMediaMode { Gap = 0, BlackMark = 1, Continuous = 2 }
