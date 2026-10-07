namespace TrainController.Abstractions.Outputs;

/// <summary>Result of one controller evaluation for one train and one tick.</summary>
public sealed record TrainControllerOutput
{
    /// <summary>Echo of the input's TrainId.</summary>
    public string TrainId { get; init; } = string.Empty;

    /// <summary>Echo of the input's TickId.</summary>
    public long TickId { get; init; }

    /// <summary>Train-Model-facing commands.</summary>
    public TrainModelCommand Commands { get; init; } = new TrainModelCommand();

    /// <summary>Driver-facing information for the Main UI.</summary>
    public DriverDisplayState Display { get; init; } = new DriverDisplayState();

    /// <summary>True when this output was produced by <see cref="FailSafeOutput"/> rather than by a controller.</summary>
    public bool IsFailSafe { get; init; }
}
