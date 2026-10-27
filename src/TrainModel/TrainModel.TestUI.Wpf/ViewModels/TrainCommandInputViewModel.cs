using TrainControl.Contracts.Messages;
using TrainModel.TestUI.Wpf.Commands;
using TrainModel.TestUI.Wpf.Services;

namespace TrainModel.TestUI.Wpf.ViewModels;

/// <summary>
/// Stands in for the Train Controller: builds a <see cref="TrainCommandMessage"/>
/// (power, brakes, doors, lights, cabin setpoint) and sends it to the Train Model.
/// Power is entered in HP for convenience and converted to watts on the wire.
///
/// Also rules on pending passenger emergency-brake requests via Approve / Deny,
/// which the Train Model honours only from the controller (never the passenger).
/// </summary>
public sealed class TrainCommandInputViewModel : TrainModelInputViewModelBase
{
    private string _powerHp = "0";
    private bool _serviceBrake;
    private bool _emergencyBrake;
    private bool _leftDoor;
    private bool _rightDoor;
    private bool _lights;
    private string _cabinSetpointF = "72";
    private string _stationAnnouncement = "Now approaching Pioneer Square. Doors open on the left.";
    private string _approvalResult = string.Empty;

    // Returns true when the selected train currently has a PENDING passenger e-brake
    // request, per the status the Train Model broadcasts (TrainToControllerMessage).
    private readonly Func<bool> _hasPendingRequest;

    public TrainCommandInputViewModel(ITrainModelMessageSender sender, Func<string> trainIdProvider,
        Func<bool> hasPendingRequest)
        : base(sender, trainIdProvider)
    {
        _hasPendingRequest = hasPendingRequest ?? (() => false);
        ApproveEmergencyBrakeCommand = new AsyncRelayCommand(() => RuleAsync(true));
        DenyEmergencyBrakeCommand = new AsyncRelayCommand(() => RuleAsync(false));
    }

    /// <summary>Controller approves the pending passenger e-brake request (engages brake).</summary>
    public AsyncRelayCommand ApproveEmergencyBrakeCommand { get; }

    /// <summary>Controller denies the pending passenger e-brake request (no engage).</summary>
    public AsyncRelayCommand DenyEmergencyBrakeCommand { get; }

    public string ApprovalResult
    {
        get => _approvalResult;
        private set => SetProperty(ref _approvalResult, value);
    }

    private async Task RuleAsync(bool approved)
    {
        // Approve/Deny only mean something when a request is actually pending for the
        // selected train. If none is pending, do NOT send anything and do NOT apply the
        // brake — to activate the emergency brake with no passenger request, the operator
        // checks the e-brake box and sends a command instead.
        if (!_hasPendingRequest())
        {
            ApprovalResult = $"No pending request for {TargetTrainId} — nothing to {(approved ? "approve" : "deny")}. " +
                             "(Use the Emergency Brake checkbox to apply it directly.)";
            return;
        }

        try
        {
            // Clear the pending request on the Train Model side.
            await Sender.SendToTrainModelAsync(new EmergencyBrakeApprovalMessage
            {
                TrainId = TargetTrainId,
                Approved = approved,
            });

            if (approved)
            {
                // Approving APPLIES the emergency brake: auto-check the e-brake box (the
                // controller is now commanding the brake) and immediately send the command
                // so it takes effect at once. From here the brake lives in the checkbox
                // state — unchecking and sending releases it.
                EmergencyBrake = true;
                await SendCurrentCommandAsync();
                ApprovalResult = $"Approved for {TargetTrainId} @ {DateTime.Now:HH:mm:ss} — emergency brake applied.";
            }
            else
            {
                ApprovalResult = $"Denied for {TargetTrainId} @ {DateTime.Now:HH:mm:ss} — request cleared, brake not applied.";
            }
        }
        catch (TrainModelSendException ex)
        {
            ApprovalResult = $"Send failed: {ex.Message}";
        }
    }

    /// <summary>Build and send the current Train Command (shared by the Send button and approval).</summary>
    private Task SendCurrentCommandAsync()
    {
        double hp = double.TryParse(PowerHp, out var h) ? h : 0.0;
        double watts = hp * 745.699871582;
        double tempF = double.TryParse(CabinSetpointF, out var t) ? t : 72.0;
        double tempC = (tempF - 32.0) * 5.0 / 9.0;

        var msg = new TrainCommandMessage
        {
            TrainId = TargetTrainId,
            PowerCommandWatts = watts,
            ServiceBrakeApplied = ServiceBrake,
            EmergencyBrakeApplied = EmergencyBrake,
            LeftDoorOpen = LeftDoor,
            RightDoorOpen = RightDoor,
            ExteriorLightsOn = Lights,
            CabinSetpointCelsius = tempC,
            StationAnnouncement = StationAnnouncement,
        };
        return Sender.SendToTrainModelAsync(msg);
    }
    public string PowerHp { get => _powerHp; set => SetProperty(ref _powerHp, value); }
    public bool ServiceBrake { get => _serviceBrake; set => SetProperty(ref _serviceBrake, value); }
    public bool EmergencyBrake { get => _emergencyBrake; set => SetProperty(ref _emergencyBrake, value); }
    public bool LeftDoor { get => _leftDoor; set => SetProperty(ref _leftDoor, value); }
    public bool RightDoor { get => _rightDoor; set => SetProperty(ref _rightDoor, value); }
    public bool Lights { get => _lights; set => SetProperty(ref _lights, value); }
    public string CabinSetpointF { get => _cabinSetpointF; set => SetProperty(ref _cabinSetpointF, value); }
    public string StationAnnouncement { get => _stationAnnouncement; set => SetProperty(ref _stationAnnouncement, value); }

    protected override async Task SendAsync()
    {
        double hp = double.TryParse(PowerHp, out var h) ? h : 0.0;
        try
        {
            await SendCurrentCommandAsync();
            Result = $"Sent command ({hp:0.#} hp) @ {DateTime.Now:HH:mm:ss}";
        }
        catch (TrainModelSendException ex)
        {
            Result = $"Send failed: {ex.Message}";
        }
    }
}
