namespace TrainController.Abstractions.Fleet;

/// <summary>Which Train Controller implementation permanently drives a train.</summary>
public enum ControllerType
{
    /// <summary>Executes locally on the Windows host (TrainController.Core).</summary>
    Software = 0,

    /// <summary>Executes on the Raspberry Pi, reached over TCP/IP.</summary>
    Hardware
}
