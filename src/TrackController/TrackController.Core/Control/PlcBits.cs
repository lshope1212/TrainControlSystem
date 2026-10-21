namespace TrackController.Core.Control;

/// <summary>Conservative fixed-point I/O codec only; all control gates live in the PLC file. Bit zero is least significant.</summary>
public static class PlcBits
{
    public const int Width = 48;
    public const double Scale = 1_000_000;

    /// <summary>Encodes a nonnegative external SI value, rounding downward at one-millionth resolution.</summary>
    public static bool[] Encode(double value)
    {
        double scaled = value * Scale;
        if (!double.IsFinite(value) || value < 0 || scaled > (1UL << Width) - 1)
        {
            throw new ArgumentException("PLC fixed-point input is outside its 48-bit range.");
        }

        ulong units = (ulong)Math.Floor(scaled);
        if (units / Scale > value && units > 0)
        {
            units--;
        }
        return Enumerable.Range(0, Width).Select(bit => (units & (1UL << bit)) != 0).ToArray();
    }

    /// <summary>Decodes PLC output bits into the existing external SI contract.</summary>
    public static double Decode(bool[] bits)
    {
        if (bits.Length != Width)
        {
            throw new ArgumentException("Expected a 48-bit PLC word.");
        }
        ulong units = 0;
        for (int bit = 0; bit < Width; bit++)
        {
            if (bits[bit])
            {
                units |= 1UL << bit;
            }
        }
        return units / Scale;
    }

}
