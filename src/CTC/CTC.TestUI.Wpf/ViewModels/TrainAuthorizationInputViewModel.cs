using System.Globalization;
using System.Windows.Input;
using CTC.TestUI.Wpf.Commands;
using CTC.TestUI.Wpf.Services;
using TrainControl.Contracts.Messages;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Simulates the Track Controller -> CTC authorization report by sending a real
/// <see cref="TrainAuthorizationStatusMessage"/>. Values are entered in the contract's SI
/// units; CTC converts them for display.
/// </summary>
public class TrainAuthorizationInputViewModel : CtcInputViewModelBase
{
    private string _trainId = "Train 1";
    private string _speedText = "10";
    private string _authorityText = "200";

    public TrainAuthorizationInputViewModel(ICtcMessageSender sender, CommunicationStatusViewModel communicationStatus)
        : base(sender, communicationStatus)
    {
        SendCommand = new AsyncRelayCommand(_ => SendAsync());
    }

    public string TrainId
    {
        get => _trainId;
        set => SetProperty(ref _trainId, value);
    }

    /// <summary>Authorized speed in m/s.</summary>
    public string SpeedText
    {
        get => _speedText;
        set => SetProperty(ref _speedText, value);
    }

    /// <summary>Authorized authority in meters.</summary>
    public string AuthorityText
    {
        get => _authorityText;
        set => SetProperty(ref _authorityText, value);
    }

    public ICommand SendCommand { get; }

    private Task SendAsync()
    {
        if (!double.TryParse(SpeedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
            || !double.TryParse(AuthorityText, NumberStyles.Float, CultureInfo.InvariantCulture, out var authority))
        {
            Result = "Enter speed (m/s) and authority (m) as numbers.";
            return Task.CompletedTask;
        }

        // Train ID is not validated here, so CTC's handling of unknown trains can be exercised.
        var message = new TrainAuthorizationStatusMessage
        {
            TrainId = TrainId.Trim(),
            AuthorizedSpeedMetersPerSecond = speed,
            AuthorizedAuthorityMeters = authority,
        };

        return SendToCtcAsync(message, $" for {message.TrainId}");
    }
}
