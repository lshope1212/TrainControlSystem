namespace TrainControl.Contracts.Enums;

/// <summary>
/// Side(s) of the train on which a station platform lies, as reported by a beacon.
/// The track data workbook's "Left/Right" station side maps to <see cref="Both"/>.
/// </summary>
public enum PlatformSide
{
    None = 0,
    Left,
    Right,
    Both
}
