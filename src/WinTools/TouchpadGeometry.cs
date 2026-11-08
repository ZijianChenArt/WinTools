using System;

namespace WinTools;

/// <summary>HID logical coordinates mapped to physical hundredths of a millimeter.</summary>
internal readonly record struct TouchpadAxis(int LogicalMin, int LogicalMax, double HimetricExtent)
{
    internal double ToHimetric(double value) => (value - LogicalMin) * HimetricExtent / (LogicalMax - (double)LogicalMin);

    internal static TouchpadAxis? FromHid(int logicalMin, int logicalMax, int physicalMin,
        int physicalMax, uint units, uint exponent)
    {
        // HID SI linear length is in centimeters; English linear length is in inches.
        // Require a pure length unit. Do not guess a scale from screen resolution.
        var unit = units;
        var factor = unit == 0x11 ? 1000d : unit == 0x13 ? 2540d : 0;
        var power = (int)(exponent & 0xF);
        if (power >= 8) power -= 16;
        var extent = (physicalMax - (double)physicalMin) * factor * Math.Pow(10, power);
        if (logicalMax <= logicalMin || factor == 0 || !double.IsFinite(extent) || extent < 100 || extent > 100000)
            return null;
        return new(logicalMin, logicalMax, extent);
    }
}

internal readonly record struct TouchpadGeometry(TouchpadAxis X, TouchpadAxis Y)
{
    internal uint Width => (uint)Math.Ceiling(X.HimetricExtent);
    internal uint Height => (uint)Math.Ceiling(Y.HimetricExtent);
}
