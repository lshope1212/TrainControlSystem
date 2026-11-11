namespace TrainControl.Contracts.Enums;

/// <summary>
/// The wayside's actual decision on a movement request (see
/// <see cref="Messages.MovementAuthorizationReport"/>). Only <see cref="Approved"/> and
/// <see cref="Restricted"/> grant movement.
/// </summary>
public enum MovementAuthorizationState
{
    /// <summary>No decision reported (e.g. the wayside holds no such request).</summary>
    None = 0,

    /// <summary>Movement is not authorized; speed and authority are zero.</summary>
    Withheld,

    /// <summary>Movement authorized as requested.</summary>
    Approved,

    /// <summary>Movement authorized with less speed and/or authority than requested.</summary>
    Restricted,
}
