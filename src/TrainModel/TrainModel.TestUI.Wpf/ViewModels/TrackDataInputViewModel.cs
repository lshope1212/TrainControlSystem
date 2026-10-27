using TrainControl.Contracts.Messages;
using TrainModel.TestUI.Wpf.Services;

namespace TrainModel.TestUI.Wpf.ViewModels;

/// <summary>
/// Stands in for the Track Model. Sends, to the selected train:
///   - the track-circuit speed command + authority,
///   - the simulation-only terrain (grade % AND elevation) and passenger demand,
///   - the beacon (station name, distance-to-stop, platform side, announcement).
/// Imperial on this panel (mph, ft); converted to SI on the wire.
/// </summary>
public sealed class TrackDataInputViewModel : TrainModelInputViewModelBase
{
    private string _commandedSpeedMph = "40";
    private string _authorityFt = "1250";
    private string _gradePercent = "0";
    private string _passengerDemand = "0";

    // Beacon fields
    private string _beaconStation = "Pioneer Square";
    private string _beaconDistanceFt = "800";
    private string _beaconSide = "Left";              // Left / Right / None
    private string _beaconAnnouncement = "Now approaching Pioneer Square.";

    public TrackDataInputViewModel(ITrainModelMessageSender sender, Func<string> trainIdProvider)
        : base(sender, trainIdProvider) { }

    public string CommandedSpeedMph { get => _commandedSpeedMph; set => SetProperty(ref _commandedSpeedMph, value); }
    public string AuthorityFt { get => _authorityFt; set => SetProperty(ref _authorityFt, value); }
    public string GradePercent { get => _gradePercent; set => SetProperty(ref _gradePercent, value); }
    public string PassengerDemand { get => _passengerDemand; set => SetProperty(ref _passengerDemand, value); }

    public string BeaconStation { get => _beaconStation; set => SetProperty(ref _beaconStation, value); }
    public string BeaconDistanceFt { get => _beaconDistanceFt; set => SetProperty(ref _beaconDistanceFt, value); }
    public string BeaconSide { get => _beaconSide; set => SetProperty(ref _beaconSide, value); }
    public string BeaconAnnouncement { get => _beaconAnnouncement; set => SetProperty(ref _beaconAnnouncement, value); }

    /// <summary>Platform-side options for the beacon dropdown.</summary>
    public IReadOnlyList<string> SideOptions { get; } = new[] { "Left", "Right", "None" };

    protected override async Task SendAsync()
    {
        double mph = double.TryParse(CommandedSpeedMph, out var s) ? s : 0.0;
        double ft = double.TryParse(AuthorityFt, out var a) ? a : 0.0;
        double grade = double.TryParse(GradePercent, out var g) ? g : 0.0;
        int demand = int.TryParse(PassengerDemand, out var d) ? d : 0;
        double beaconDistFt = double.TryParse(BeaconDistanceFt, out var bd) ? bd : 0.0;

        var side = BeaconSide switch
        {
            "Left" => PlatformSide.Left,
            "Right" => PlatformSide.Right,
            _ => PlatformSide.None,
        };

        var speedMsg = new SpeedCommandMessage
        {
            TrainId = TargetTrainId,
            CommandedSpeedMetersPerSecond = mph / 2.2369362920544,
        };
        var authMsg = new AuthorityMessage
        {
            TrainId = TargetTrainId,
            AuthorityMeters = ft / 3.28083989501312,
        };
        var condMsg = new TrackConditionMessage
        {
            TrainId = TargetTrainId,
            GradePercent = grade,
        };
        var paxMsg = new StationPassengerMessage
        {
            TrainId = TargetTrainId,
            WaitingToBoard = demand,
        };
        var beaconMsg = new BeaconMessage
        {
            TrainId = TargetTrainId,
            StationName = BeaconStation,
            DistanceToStopMeters = beaconDistFt / 3.28083989501312,
            Side = side,
            Announcement = BeaconAnnouncement,
        };

        try
        {
            await Sender.SendToTrainModelAsync(speedMsg);
            await Sender.SendToTrainModelAsync(authMsg);
            await Sender.SendToTrainModelAsync(condMsg);
            await Sender.SendToTrainModelAsync(paxMsg);
            await Sender.SendToTrainModelAsync(beaconMsg);
            Result = $"Sent track data + beacon ({mph:0.#} mph, {ft:0} ft, {grade:0.#}% grade, beacon '{BeaconStation}') @ {DateTime.Now:HH:mm:ss}";
        }
        catch (TrainModelSendException ex)
        {
            Result = $"Send failed: {ex.Message}";
        }
    }
}
