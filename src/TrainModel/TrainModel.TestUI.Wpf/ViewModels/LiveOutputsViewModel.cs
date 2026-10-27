namespace TrainModel.TestUI.Wpf.ViewModels;

/// <summary>
/// Latest live values of the Train Model's outputs, split by destination (to the
/// Track Model and to the Train Controller). Updated as captured messages arrive.
/// Imperial where the console shows imperial.
/// </summary>
public sealed class LiveOutputsViewModel : ViewModelBase
{
    // ---- Outputs -> Track Model (position, actual speed, passenger exchange) ----
    private string _tmActualSpeed = "--";
    private string _tmPosition = "--";
    private string _tmPassengerExchange = "--";
    private string _tmLastUpdate = "never";

    public string TmActualSpeed { get => _tmActualSpeed; set => SetProperty(ref _tmActualSpeed, value); }
    public string TmPosition { get => _tmPosition; set => SetProperty(ref _tmPosition, value); }
    public string TmPassengerExchange { get => _tmPassengerExchange; set => SetProperty(ref _tmPassengerExchange, value); }
    public string TmLastUpdate { get => _tmLastUpdate; set => SetProperty(ref _tmLastUpdate, value); }

    // ---- Outputs -> Train Controller ----
    // actual speed + cabin temp + pass-throughs (commanded speed/authority, signal,
    // beacon) + passenger emergency brake status.
    private string _tcActualSpeed = "--";
    private string _tcCabinTemp = "--";
    private string _tcCommandedSpeed = "--";
    private string _tcCommandedAuthority = "--";
    private string _tcTrackSignal = "--";
    private string _tcBeacon = "--";
    private string _tcPassengerEBrake = "--";
    private string _tcLastUpdate = "never";

    public string TcActualSpeed { get => _tcActualSpeed; set => SetProperty(ref _tcActualSpeed, value); }
    public string TcCabinTemp { get => _tcCabinTemp; set => SetProperty(ref _tcCabinTemp, value); }
    public string TcCommandedSpeed { get => _tcCommandedSpeed; set => SetProperty(ref _tcCommandedSpeed, value); }
    public string TcCommandedAuthority { get => _tcCommandedAuthority; set => SetProperty(ref _tcCommandedAuthority, value); }
    public string TcTrackSignal { get => _tcTrackSignal; set => SetProperty(ref _tcTrackSignal, value); }
    public string TcBeacon { get => _tcBeacon; set => SetProperty(ref _tcBeacon, value); }
    public string TcPassengerEBrake { get => _tcPassengerEBrake; set => SetProperty(ref _tcPassengerEBrake, value); }
    public string TcLastUpdate { get => _tcLastUpdate; set => SetProperty(ref _tcLastUpdate, value); }
}
