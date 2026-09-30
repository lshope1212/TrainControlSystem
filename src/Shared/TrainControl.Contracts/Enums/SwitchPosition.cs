namespace TrainControl.Contracts.Enums;

/// <summary>
/// Placeholder position of a track switch.
/// </summary>
/// <remarks>
/// TODO: The requirements documentation uses both Left/Right and Normal/Reverse
/// terminology for switch positions. The Track Controller team has not yet decided
/// which is canonical. Keep Normal/Reverse until that is resolved so other modules
/// are not broken by a rename.
/// </remarks>
public enum SwitchPosition
{
    Unknown = 0,
    Normal,
    Reverse
}
