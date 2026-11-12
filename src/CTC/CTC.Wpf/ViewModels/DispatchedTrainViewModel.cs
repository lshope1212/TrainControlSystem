using System.Collections.ObjectModel;
using CTC.Core.Dispatching;
using CTC.Core.Models;
using CTC.Core.Scheduling;
using TrainControl.Common.Utilities;
using TrainControl.Contracts.Enums;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// Presentation of a <see cref="DispatchedTrainState"/> for the Dispatched Trains grid. Shows
/// the train's active movement plan and the LATEST speed/authority CTC suggested (at release,
/// or after the dispatcher changed the plan); what the Track Controller actually did with the
/// request is shown separately in <see cref="WaysideStatus"/>.
/// Read-only: the plan (its <see cref="Stops"/>) is changed through <see cref="MainWindowViewModel.ApplyTrainPlanCommand"/>.
/// CTC stores SI units; the grid shows imperial units.
/// </summary>
public class DispatchedTrainViewModel : ViewModelBase
{
    private readonly DispatchedTrainState _train;

    public DispatchedTrainViewModel(DispatchedTrainState train)
    {
        _train = train ?? throw new ArgumentNullException(nameof(train));
        Refresh();
    }

    public string TrainId => _train.TrainId;

    /// <summary>Dispatcher-facing label, e.g. "Train 000".</summary>
    public string TrainDisplayName => TrainIds.DisplayName(_train.TrainId);

    /// <summary>CTC's latest suggested speed, in mph.</summary>
    public double SuggestedSpeed => UnitConversion.MetersPerSecondToMilesPerHour(_train.SuggestedSpeedMetersPerSecond);

    /// <summary>CTC's latest suggested authority, in feet.</summary>
    public double SuggestedAuthority => UnitConversion.MetersToFeet(_train.SuggestedAuthorityMeters);

    /// <summary>Block CTC infers the train is in from sequential track occupancy (see <see cref="DispatchedTrainState.CurrentBlockId"/>).</summary>
    public string CurrentBlock => string.IsNullOrEmpty(_train.CurrentBlockId) ? "Unknown" : _train.CurrentBlockId;

    /// <summary>Block the latest suggestion is planned to reach.</summary>
    public string TargetBlockId => _train.TargetBlockId;

    /// <summary>When the train is planned to enter <see cref="TargetBlockId"/>.</summary>
    public TimeSpan TargetArrivalTime => _train.TargetArrivalTime;

    /// <summary><see cref="TargetArrivalTime"/> as HH:mm:ss, matching the simulation clock.</summary>
    public string TargetArrivalTimeDisplay => _train.TargetArrivalTime.ToString(@"hh\:mm\:ss");

    /// <summary>Planned dwell at <see cref="TargetBlockId"/>, in whole seconds.</summary>
    public string TargetDwellSecondsDisplay => DwellTimePolicy.FormatSeconds(_train.TargetDwellTime);

    /// <summary>Where the train is relative to its current stop, e.g. "Dwelling until 12:01:45".</summary>
    public string MovementStatus => _train.Phase == TrainMovementPhase.Dwelling && _train.ReadyToDepartTime is { } ready
        ? $@"Dwelling until {ready:hh\:mm\:ss}"
        : DescribePhase(_train.Phase);

    /// <summary>
    /// Whether the released train has been seen on the track, and how far its physical dispatch got.
    /// The dispatch's stages (delivered, forwarded, activation requested, placed, rejected) are shown as
    /// such; the train is shown on the track only once its start block reports occupied.
    /// </summary>
    public string ReleaseStatus
    {
        get
        {
            if (_train.ReleaseStatus == TrainReleaseStatus.DetectedOnTrack)
            {
                return _train.TrackingStatus.Length > 0 ? $"Detected on track (inferred) · Track Model: {_train.TrackingStatus}" : "Detected on track (inferred)";
            }

            string start = _train.ActiveRouteBlockIds.Count > 0 ? _train.ActiveRouteBlockIds[0] : _train.CurrentBlockId;
            return _train.DispatchRequest is { } dispatch
                ? $"Awaiting placement at {start}: dispatch {TrainDispatchCoordinator.DescribeStage(dispatch)}"
                : $"Awaiting placement at {start}";
        }
    }

    /// <summary>
    /// The train's movement request as the Track Controller handled it, kept apart from delivery:
    /// sent/awaiting acknowledgement, rejected (with the reason), accepted for evaluation, or the
    /// wayside's actual authorization (speed in mph, authority in feet) or why it was withheld.
    /// </summary>
    public string WaysideStatus
    {
        get
        {
            if (_train.MovementRequest is not { } request)
            {
                return _train.ReleaseStatus == TrainReleaseStatus.AwaitingPlacement ? "No request (awaiting placement)" : "No request";
            }

            if (request.IsRetired)
                return $"Request for {request.Plan.TargetBlockId} ended. Waiting for current block authority.";

            string status = request.Delivery switch
            {
                MovementRequestDelivery.Failed => $"Not delivered: {request.DeliveryError}",
                MovementRequestDelivery.NotSent => "Not sent yet",
                _ => request.Acknowledgement switch
                {
                    null => "Sent; awaiting acknowledgement",
                    RequestAcknowledgementOutcome.Rejected => $"Rejected: {request.AcknowledgementDetail}",
                    _ => request.Authorization switch
                    {
                        MovementAuthorizationState.Approved => $"Authorized {Authorized(request)}",
                        MovementAuthorizationState.Restricted => $"Restricted to {Authorized(request)}",
                        MovementAuthorizationState.Withheld => $"Withheld: {request.AuthorizationReason}",
                        _ => request.Acknowledgement == RequestAcknowledgementOutcome.PreparedAsSuccessor ? "Prepared as successor" : "Accepted for evaluation",
                    },
                },
            };

            // The prepared handoff for the next block is shown, never as an authorization.
            return _train.StagedMovementRequest is { Acknowledgement: RequestAcknowledgementOutcome.PreparedAsSuccessor } staged
                ? $"{status} · handoff to {staged.Plan.TargetBlockId} prepared"
                : status;
        }
    }

    private static string Authorized(MovementRequestState request) => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        "{0:F0} ft at {1:F1} mph",
        UnitConversion.MetersToFeet(request.AuthorizedAuthorityMeters),
        UnitConversion.MetersPerSecondToMilesPerHour(request.AuthorizedSpeedMetersPerSecond));

    /// <summary>The remaining stops of the Active Movement Plan, current stop first.</summary>
    public ObservableCollection<MovementPlanStopViewModel> Stops { get; } = new ObservableCollection<MovementPlanStopViewModel>();

    /// <summary>Re-reads all values from the underlying CTC train state.</summary>
    public void Refresh()
    {
        Stops.Clear();
        for (int i = 0; i < _train.Stops.Count; i++)
        {
            Stops.Add(new MovementPlanStopViewModel(_train, i));
        }

        OnPropertyChanged(string.Empty);
    }

    internal static string DescribePhase(TrainMovementPhase phase) => phase switch
    {
        TrainMovementPhase.EnRoute => "En Route",
        TrainMovementPhase.Arrived => "Arrived (ready to proceed)",
        TrainMovementPhase.Dwelling => "Dwelling",
        TrainMovementPhase.Completed => "Completed",
        _ => phase.ToString(),
    };
}
