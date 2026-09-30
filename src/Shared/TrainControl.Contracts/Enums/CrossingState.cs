namespace TrainControl.Contracts.Enums;

/// <summary>
/// State of a railway crossing (gates/lights) as reported by the wayside.
/// </summary>
public enum CrossingState
{
    Unknown = 0,
    Open,
    Closed
}
