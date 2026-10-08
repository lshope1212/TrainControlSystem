namespace TrainControl.Contracts.Enums;

/// <summary>
/// Placeholder wayside signal aspects.
/// </summary>
public enum SignalState
{
    Unknown = 0,
    Red,
    Yellow,
    Green,

    /// <summary>No additional signal speed cap; track limits and movement authority still apply.</summary>
    SuperGreen
}
