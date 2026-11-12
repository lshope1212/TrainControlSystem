using System.Windows.Threading;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;
using TrainModel.Wpf.ViewModels;

namespace TrainModel.Wpf.Services;

/// <summary>
/// The Train Model's inbound endpoint. Hosts the <see cref="NamedPipeNames.TrainModel"/>
/// pipe server and routes each received shared-contract message into the
/// <see cref="MainWindowViewModel"/> (and thus the per-train fleet), on the WPF UI
/// thread. Routing is by the TrainId carried on every message.
///
/// Its only job is envelope -> contract -> matching VM apply call; all simulation
/// behavior stays in TrainModel.Core. It does not know whether the sender is a real
/// neighbour module or TrainModel.TestUI.Wpf.
/// </summary>
public sealed class TrainModelNamedPipeReceiver
{
    private readonly MainWindowViewModel _vm;
    private readonly Dispatcher _dispatcher;
    private readonly string _pipeName;

    /// <param name="pipeName">Inbound pipe; <see cref="NamedPipeNames.TrainModel"/> unless a test isolates it.</param>
    public TrainModelNamedPipeReceiver(MainWindowViewModel vm, Dispatcher dispatcher, string? pipeName = null)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? NamedPipeNames.TrainModel : pipeName;
    }

    /// <summary>Readable outcome of each received message (or receive error), on the UI thread.</summary>
    public event EventHandler<string>? StatusReported;

    public Task RunAsync(CancellationToken cancellationToken) =>
        NamedPipeTransport.ListenAsync(_pipeName, HandleAsync, ReportError, cancellationToken);

    private Task HandleAsync(MessageEnvelope envelope)
    {
        // Deserialize on the background thread; the VM mutation runs on the UI thread.
        Action? apply = envelope.MessageType switch
        {
            // The Train Controller's per-tick command (her contract + field names). Replaces
            // the old TrainCommandMessage. TickId is accepted and echoed on the status message.
            nameof(TrainControllerCommandMessage) => Bind<TrainControllerCommandMessage>(envelope, m =>
                _vm.ApplyTrainCommand(m.TrainId, m.TickId, m.PowerCommandWatts, m.ServiceBrakeCommand,
                    m.EmergencyBrakeCommand, m.LeftDoorsOpenCommand, m.RightDoorsOpenCommand,
                    m.ExteriorLightsCommand, m.CabinTemperatureSetpointCelsius, m.StationAnnouncement)),

            // Shared simulation clock (Launcher / CTC, or the TestUI test clock).
            nameof(SystemTimeMessage) => Bind<SystemTimeMessage>(envelope, m => _vm.ApplySystemTime(m)),
            // Physical dispatch: the Track Model activates (or retires) a train at cumulative position 0.
            nameof(TrainActivationMessage) => Bind<TrainActivationMessage>(envelope, m => _vm.ApplyTrainActivation(m)),

            // Legacy/TestUI placement by block (no position telemetry contract); kept for the standalone TestUI.
            nameof(TrackModelTrainUpdateMessage) => Bind<TrackModelTrainUpdateMessage>(envelope, m =>
                _vm.ApplyTrainPlacement(m.TrainId, m.CurrentBlockId, m.ActualSpeedMetersPerSecond)),
            nameof(SpeedCommandMessage) => Bind<SpeedCommandMessage>(envelope, m =>
                _vm.ApplyCommandedSpeed(m.TrainId, m.CommandedSpeedMetersPerSecond)),

            nameof(AuthorityMessage) => Bind<AuthorityMessage>(envelope, m =>
                _vm.ApplyCommandedAuthority(m.TrainId, m.AuthorityMeters)),

            // The Track Model's track-circuit data, one message per block per snapshot. Only the block
            // naming a train in service here applies (newest snapshot first; see TrackCircuitReceiver).
            nameof(TrackModelTrainEnvironmentMessage) => Bind<TrackModelTrainEnvironmentMessage>(envelope, m =>
                _vm.ApplyTrackEnvironment(m)),

            nameof(TrackConditionMessage) => Bind<TrackConditionMessage>(envelope, m =>
                _vm.ApplyTrackCondition(m.TrainId, m.GradePercent)),

            nameof(BeaconMessage) => Bind<BeaconMessage>(envelope, m =>
                _vm.ApplyBeacon(m.TrainId, m.StationName, m.DistanceToStopMeters, m.Side.ToString(), m.Announcement, m.ReadSequence)),

            nameof(StationPassengerMessage) => Bind<StationPassengerMessage>(envelope, m =>
                _vm.ApplyPassengerDemand(m.TrainId, m.WaitingToBoard)),

            nameof(TrainFailureMessage) => Bind<TrainFailureMessage>(envelope, m =>
                _vm.ApplyFailures(m.TrainId, m.EngineFailure, m.BrakeFailure, m.SignalPickupFailure)),

            nameof(PassengerEmergencyBrakeMessage) => Bind<PassengerEmergencyBrakeMessage>(envelope, m =>
                _vm.ApplyPassengerEmergencyBrake(m.TrainId, m.Requested)),

            _ => null,
        };

        if (apply is null)
        {
            Post(() => StatusReported?.Invoke(this, $"Ignored inbound message of unknown type '{envelope.MessageType}'."));
            return Task.CompletedTask;
        }

        // Queue the change on the UI thread WITHOUT waiting for it: the pipe is free to accept
        // the next message at once (the UI keeps the arrival order).
        Post(apply);
        return Task.CompletedTask;
    }

    private void Post(Action action) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);

    /// <summary>Deserialize the payload and bind it into a UI-thread action, or report a bad payload.</summary>
    private Action? Bind<TMessage>(MessageEnvelope envelope, Action<TMessage> apply)
        where TMessage : class
    {
        try
        {
            var message = MessageSerializer.DeserializePayload<TMessage>(envelope);
            return () => apply(message);
        }
        catch (Exception ex)
        {
            return () => StatusReported?.Invoke(this, $"Malformed {typeof(TMessage).Name}: {ex.Message}");
        }
    }

    private void ReportError(Exception exception) =>
        Post(() => StatusReported?.Invoke(this, $"Receive error: {exception.Message}"));
}
