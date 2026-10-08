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
    SuperGreen
}

public static class SignalStateExtensions
{
    /// <summary>Dispatcher-facing text for a signal aspect, e.g. <see cref="SignalState.SuperGreen"/> -> "Super Green".</summary>
    public static string ToDisplayName(this SignalState signal) => signal switch
    {
        SignalState.SuperGreen => "Super Green",
        _ => signal.ToString(),
    };
}
