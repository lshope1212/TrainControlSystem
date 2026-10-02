using CTC.TestUI.Wpf.Services;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Root view model of the CTC Test UI, which stands in for the modules external to CTC.
/// Composes one small view model per area: simulated inputs, captured CTC outputs and
/// communication status. New input simulators should be added as their own view models.
/// </summary>
/// <remarks>
/// The TestUI has no access to CTC.Core. Inputs reach the running CTC only as shared
/// contract messages over its named pipe; outputs are only what CTC actually sends to the
/// fake Track Controller endpoint. Dispatcher actions belong to the real CTC window.
/// </remarks>
public class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(ICtcMessageSender sender, FakeTrackControllerReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(receiver);

        Communication = new CommunicationStatusViewModel(receiver);
        Outputs = new CapturedOutputsViewModel(receiver);

        // Simulated inputs (other modules -> CTC).
        TrackLayoutInput = new TrackLayoutInputViewModel(sender, Communication);
        BlockStatusInput = new BlockStatusInputViewModel(sender, Communication);
        SystemTimeInput = new SystemTimeInputViewModel(sender, Communication);
    }

    public string Title => "CTC Module Test UI";

    // Simulated Input ViewModels
    public TrackLayoutInputViewModel TrackLayoutInput { get; }

    public SystemTimeInputViewModel SystemTimeInput { get; }

    public BlockStatusInputViewModel BlockStatusInput { get; }

    // Other
    public CapturedOutputsViewModel Outputs { get; }

    public CommunicationStatusViewModel Communication { get; }
}
