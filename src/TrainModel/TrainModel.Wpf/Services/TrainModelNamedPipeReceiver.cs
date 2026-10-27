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

    public TrainModelNamedPipeReceiver(MainWindowViewModel vm, Dispatcher dispatcher)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>Readable outcome of each received message (or receive error), on the UI thread.</summary>
    public event EventHandler<string>? StatusReported;

    public Task RunAsync(CancellationToken cancellationToken) =>
        NamedPipeTransport.ListenAsync(NamedPipeNames.TrainModel, HandleAsync, ReportError, cancellationToken);

    private async Task HandleAsync(MessageEnvelope envelope)
    {
        // Deserialize on the background thread; the VM mutation runs on the UI thread.
        Action? apply = envelope.MessageType switch
        {
            nameof(TrainCommandMessage) => Bind<TrainCommandMessage>(envelope, m =>
                _vm.ApplyTrainCommand(m.TrainId, m.PowerCommandWatts, m.ServiceBrakeApplied,
                    m.EmergencyBrakeApplied, m.LeftDoorOpen, m.RightDoorOpen, m.ExteriorLightsOn,
                    m.CabinSetpointCelsius, m.StationAnnouncement)),

            nameof(SpeedCommandMessage) => Bind<SpeedCommandMessage>(envelope, m =>
                _vm.ApplyCommandedSpeed(m.TrainId, m.CommandedSpeedMetersPerSecond)),

            nameof(AuthorityMessage) => Bind<AuthorityMessage>(envelope, m =>
                _vm.ApplyCommandedAuthority(m.TrainId, m.AuthorityMeters)),

            nameof(TrackConditionMessage) => Bind<TrackConditionMessage>(envelope, m =>
                _vm.ApplyTrackCondition(m.TrainId, m.GradePercent)),

            nameof(BeaconMessage) => Bind<BeaconMessage>(envelope, m =>
                _vm.ApplyBeacon(m.TrainId, m.StationName, m.DistanceToStopMeters, m.Side.ToString(), m.Announcement)),

            nameof(StationPassengerMessage) => Bind<StationPassengerMessage>(envelope, m =>
                _vm.ApplyPassengerDemand(m.TrainId, m.WaitingToBoard)),

            nameof(TrainFailureMessage) => Bind<TrainFailureMessage>(envelope, m =>
                _vm.ApplyFailures(m.TrainId, m.EngineFailure, m.BrakeFailure, m.SignalPickupFailure)),

            nameof(PassengerEmergencyBrakeMessage) => Bind<PassengerEmergencyBrakeMessage>(envelope, m =>
                _vm.ApplyPassengerEmergencyBrake(m.TrainId, m.Requested)),

            nameof(EmergencyBrakeApprovalMessage) => Bind<EmergencyBrakeApprovalMessage>(envelope, m =>
                _vm.ApplyEmergencyBrakeApproval(m.TrainId, m.Approved)),

            _ => null,
        };

        if (apply is null)
        {
            await ReportOnUiThreadAsync($"Ignored inbound message of unknown type '{envelope.MessageType}'.");
            return;
        }

        await _dispatcher.InvokeAsync(apply);
    }

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

    private async Task ReportOnUiThreadAsync(string message) =>
        await _dispatcher.InvokeAsync(() => StatusReported?.Invoke(this, message));

    private void ReportError(Exception exception) =>
        _dispatcher.InvokeAsync(() => StatusReported?.Invoke(this, $"Receive error: {exception.Message}"));
}
