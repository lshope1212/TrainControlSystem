using CTC.Core.Interfaces;
using CTC.TestUI.Wpf.Services;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Root view model of the CTC Test UI. Composes one small view model per area:
/// simulated inputs, dispatcher actions, internal CTC state and captured outputs.
/// New input simulators / actions should be added as their own view models here.
/// </summary>
/// <remarks>
/// Every child talks to the real CTC.Core through <see cref="ICTCService"/>. None of
/// them may re-implement CTC behavior; they only build contract messages, call the
/// service and display results.
/// </remarks>
public class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(ICTCService ctc, RecordingMessageSender sender)
    {
        ArgumentNullException.ThrowIfNull(ctc);
        ArgumentNullException.ThrowIfNull(sender);

        State = new CtcStateViewModel(ctc.State);
        Outputs = new CapturedOutputsViewModel(sender);

        // Simulated inputs (other modules -> CTC).
        TrackLayoutInput = new TrackLayoutInputViewModel(ctc, State.Refresh);
        SystemTimeInput = new SystemTimeInputViewModel(ctc, State.Refresh);

        // Dispatcher / CTC actions (CTC -> other modules).
        BlockMaintenanceAction = new BlockMaintenanceActionViewModel(ctc, State.Refresh);
    }

    public string Title => "CTC Module Test UI";

    public TrackLayoutInputViewModel TrackLayoutInput { get; }

    public SystemTimeInputViewModel SystemTimeInput { get; }

    public BlockMaintenanceActionViewModel BlockMaintenanceAction { get; }

    public CtcStateViewModel State { get; }

    public CapturedOutputsViewModel Outputs { get; }
}
