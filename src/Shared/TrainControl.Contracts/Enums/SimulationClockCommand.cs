namespace TrainControl.Contracts.Enums;

/// <summary>Operation requested of the shared simulation clock (see SimulationTimeControlMessage).</summary>
public enum SimulationClockCommand
{
    /// <summary>Run, or resume after a pause.</summary>
    Run,

    Pause,

    /// <summary>Change the playback rate (1..10).</summary>
    SetSpeed,

    /// <summary>Jump to a time of day.</summary>
    SetTime,

    /// <summary>Return to the startup state: start time, paused, 1x.</summary>
    Reset,
}
