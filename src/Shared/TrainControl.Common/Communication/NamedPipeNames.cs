namespace TrainControl.Common.Communication;

/// <summary>
/// Local named-pipe endpoints. Each name identifies the process that hosts the pipe
/// server (i.e. RECEIVES on it); any other process sends to it as a client.
/// Endpoint names only: no business logic belongs here.
/// </summary>
public static class NamedPipeNames
{
    /// <summary>Inbound endpoint of the CTC process.</summary>
    public const string Ctc = "TrainControl.CTC";

    /// <summary>Inbound endpoint of the Track Controller process.</summary>
    public const string TrackController = "TrainControl.TrackController";

    public const string TrackModel = "TrainControl.TrackModel";
    public const string TrackModelTestUi = "TrainControl.TrackModel.TestUI";
    public const string TrainModel = "TrainControl.TrainModel";
    public const string TrainController = "TrainControl.TrainController";
}
