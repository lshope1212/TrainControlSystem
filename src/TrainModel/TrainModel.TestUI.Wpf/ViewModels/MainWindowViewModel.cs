using System.Collections.ObjectModel;
using System.Windows.Threading;
using TrainControl.Common.Communication;
using TrainModel.TestUI.Wpf.Services;

namespace TrainModel.TestUI.Wpf.ViewModels;

/// <summary>
/// Root view model for the Train Model TestUI. Composes the input panels (each a
/// simulated neighbour of the Train Model), the captured-output grid, and the
/// communication status. Runs beside TrainModel.Wpf: it SENDS shared contract
/// messages into the real Train Model and CAPTURES what the Train Model sends to
/// its own neighbours.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        ITrainModelMessageSender sender = new NamedPipeTrainModelMessageSender();

        Outputs = new CapturedOutputsViewModel(dispatcher);
        LiveOutputs = new LiveOutputsViewModel();
        Communication = new CommunicationStatusViewModel();

        // One shared target-train selector drives every panel.
        Func<string> trainId = () => SelectedTrainId;
        Command = new TrainCommandInputViewModel(sender, trainId,
            () => string.Equals(LiveOutputs.TcPassengerEBrake, "Pending", StringComparison.OrdinalIgnoreCase));
        Track = new TrackDataInputViewModel(sender, trainId);
        SystemTime = new SystemTimeInputViewModel(sender, trainId);
    }

    public string Title => "Train Model TestUI";

    /// <summary>Train ids the harness knows about (matches the Train Model's seeded
    /// fleet). The selector is editable, so any id can also be typed in.</summary>
    public ObservableCollection<string> KnownTrainIds { get; } =
        new() { "T04", "T08", "T12" };

    private string _selectedTrainId = "T04";
    /// <summary>The single target train every input panel sends to.</summary>
    public string SelectedTrainId
    {
        get => _selectedTrainId;
        set => SetProperty(ref _selectedTrainId, string.IsNullOrWhiteSpace(value) ? _selectedTrainId : value.Trim());
    }

    public TrainCommandInputViewModel Command { get; }
    public TrackDataInputViewModel Track { get; }
    public SystemTimeInputViewModel SystemTime { get; }

    public CapturedOutputsViewModel Outputs { get; }
    public LiveOutputsViewModel LiveOutputs { get; }
    public CommunicationStatusViewModel Communication { get; }

    /// <summary>Turn a captured envelope into a readable grid row.</summary>
    /// <summary>A message captured on the TRACK MODEL pipe (Train Model -> Track Model).</summary>
    public void RecordTrackBound(MessageEnvelope envelope)
    {
        Outputs.Record($"{envelope.MessageType}  ->Track", DescribeEnvelope(envelope));
        UpdateTrackLive(envelope);
    }

    /// <summary>A message captured on the TRAIN CONTROLLER pipe (Train Model -> Train Controller).</summary>
    public void RecordControllerBound(MessageEnvelope envelope)
    {
        Outputs.Record($"{envelope.MessageType}  ->Controller", DescribeEnvelope(envelope));
        UpdateControllerLive(envelope);
    }

    /// <summary>Back-compat shim: route to the generic log without a known destination.</summary>
    public void RecordCapturedMessage(MessageEnvelope envelope)
    {
        Outputs.Record(envelope.MessageType, DescribeEnvelope(envelope));
    }

    private void UpdateTrackLive(MessageEnvelope envelope)
    {
        try
        {
            switch (envelope.MessageType)
            {
                case nameof(TrainControl.Contracts.Messages.TrainStateMessage):
                {
                    var m = MessageSerializer.DeserializePayload<TrainControl.Contracts.Messages.TrainStateMessage>(envelope);
                    if (!IsSelected(m.TrainId)) break;   // only the selected train drives the live display
                    LiveOutputs.TmActualSpeed = $"{m.SpeedMetersPerSecond * 2.2369362920544:0.0} mph";
                    LiveOutputs.TmPosition = $"{m.PositionMeters * 3.28083989501312:0} ft";
                    LiveOutputs.TmLastUpdate = DateTime.Now.ToString("HH:mm:ss");
                    break;
                }
                case nameof(TrainControl.Contracts.Messages.PassengerExchangeMessage):
                {
                    var m = MessageSerializer.DeserializePayload<TrainControl.Contracts.Messages.PassengerExchangeMessage>(envelope);
                    if (!IsSelected(m.TrainId)) break;
                    LiveOutputs.TmPassengerExchange = $"{m.Boarded} boarded / {m.Alighted} alighted / {m.LeftBehind} left";
                    LiveOutputs.TmLastUpdate = DateTime.Now.ToString("HH:mm:ss");
                    break;
                }
            }
        }
        catch { /* malformed payload: leave last-good values */ }
    }

    /// <summary>True if a message's train id matches the currently selected train (case-insensitive).</summary>
    private bool IsSelected(string? trainId) =>
        !string.IsNullOrWhiteSpace(trainId) &&
        string.Equals(trainId.Trim(), SelectedTrainId?.Trim(), StringComparison.OrdinalIgnoreCase);

    private void UpdateControllerLive(MessageEnvelope envelope)
    {
        try
        {
            switch (envelope.MessageType)
            {
                case nameof(TrainControl.Contracts.Messages.TrainStateMessage):
                {
                    var m = MessageSerializer.DeserializePayload<TrainControl.Contracts.Messages.TrainStateMessage>(envelope);
                    if (!IsSelected(m.TrainId)) break;
                    LiveOutputs.TcActualSpeed = $"{m.SpeedMetersPerSecond * 2.2369362920544:0.0} mph";
                    LiveOutputs.TcLastUpdate = DateTime.Now.ToString("HH:mm:ss");
                    break;
                }
                case nameof(TrainControl.Contracts.Messages.TrainCabinStatusMessage):
                {
                    var m = MessageSerializer.DeserializePayload<TrainControl.Contracts.Messages.TrainCabinStatusMessage>(envelope);
                    if (!IsSelected(m.TrainId)) break;
                    LiveOutputs.TcCabinTemp = $"{m.CabinTemperatureCelsius * 9.0 / 5.0 + 32.0:0.0} °F";
                    LiveOutputs.TcLastUpdate = DateTime.Now.ToString("HH:mm:ss");
                    break;
                }
                case nameof(TrainControl.Contracts.Messages.TrainToControllerMessage):
                {
                    var m = MessageSerializer.DeserializePayload<TrainControl.Contracts.Messages.TrainToControllerMessage>(envelope);
                    if (!IsSelected(m.TrainId)) break;
                    LiveOutputs.TcCommandedSpeed = $"{m.CommandedSpeedMetersPerSecond * 2.2369362920544:0.0} mph";
                    LiveOutputs.TcCommandedAuthority = $"{m.CommandedAuthorityMeters * 3.28083989501312:0} ft";
                    LiveOutputs.TcTrackSignal = m.TrackSignalStatus;
                    LiveOutputs.TcBeacon = string.IsNullOrWhiteSpace(m.BeaconStationName)
                        ? "--"
                        : $"{m.BeaconStationName} · {m.BeaconDistanceToStopMeters * 3.28083989501312:0} ft · {m.BeaconPlatformSide}";
                    LiveOutputs.TcPassengerEBrake = m.PassengerEmergencyBrakeStatus;
                    LiveOutputs.TcLastUpdate = DateTime.Now.ToString("HH:mm:ss");
                    break;
                }
            }
        }
        catch { /* malformed payload: leave last-good values */ }
    }

    private static string DescribeEnvelope(MessageEnvelope envelope)
    {
        // Best-effort readable summary for the common Train Model outputs.
        try
        {
            switch (envelope.MessageType)
            {
                case nameof(TrainControl.Contracts.Messages.TrainStateMessage):
                {
                    var m = MessageSerializer.DeserializePayload<TrainControl.Contracts.Messages.TrainStateMessage>(envelope);
                    double mph = m.SpeedMetersPerSecond * 2.2369362920544;
                    double ft = m.PositionMeters * 3.28083989501312;
                    return $"{m.TrainId}: speed {mph:0.0} mph, pos {ft:0} ft, accel {m.AccelerationMetersPerSecondSquared:0.00} m/s²";
                }
                case nameof(TrainControl.Contracts.Messages.PassengerExchangeMessage):
                {
                    var m = MessageSerializer.DeserializePayload<TrainControl.Contracts.Messages.PassengerExchangeMessage>(envelope);
                    return $"{m.TrainId}: {m.Boarded} boarded / {m.Alighted} alighted";
                }
                case nameof(TrainControl.Contracts.Messages.TrainCabinStatusMessage):
                {
                    var m = MessageSerializer.DeserializePayload<TrainControl.Contracts.Messages.TrainCabinStatusMessage>(envelope);
                    double f = m.CabinTemperatureCelsius * 9.0 / 5.0 + 32.0;
                    return $"{m.TrainId}: doors {(m.LeftDoorOpen ? "L" : "-")}{(m.RightDoorOpen ? "R" : "-")}, lights {(m.ExteriorLightsOn ? "on" : "off")}, cabin {f:0.0} °F";
                }
                default:
                    return $"(payload: {envelope.Payload})";
            }
        }
        catch
        {
            return "(unparsed payload)";
        }
    }
}
