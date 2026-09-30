using System.Windows.Input;
using CTC.Core.Interfaces;
using CTC.Wpf.Commands;
using TrainControl.Contracts.Enums;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// View model for the CTC office window. Translates user intent into CTC service calls;
/// it never builds outgoing contract messages itself.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly ICTCService _ctc;
    private BlockViewModel? _selectedBlock;
    private string _communicationStatus = "No requests sent.";

    public MainWindowViewModel(ICTCService ctc)
    {
        _ctc = ctc ?? throw new ArgumentNullException(nameof(ctc));

        CloseSelectedBlockCommand = new AsyncRelayCommand(_ => CloseSelectedBlockAsync(), _ => CanCloseSelectedBlock());

        //TODO other command implementations
    }

    public string Title => "CTC Office";

    public string Status => "Architecture skeleton — no dispatching implemented yet.";

    public int TrainCount => _ctc.State.DispatchedTrains.Count;

    /// <summary>
    /// Block chosen by the dispatcher. Intended to be set by the territory display
    /// once it renders CTC blocks.
    /// </summary>
    public BlockViewModel? SelectedBlock
    {
        get => _selectedBlock;
        set => SetProperty(ref _selectedBlock, value);
    }

    /// <summary>Result of the most recent request sent to another subsystem.</summary>
    public string CommunicationStatus
    {
        get => _communicationStatus;
        private set => SetProperty(ref _communicationStatus, value);
    }

    public ICommand CloseSelectedBlockCommand { get; }

    private bool CanCloseSelectedBlock() => SelectedBlock is not null && SelectedBlock.RequestedMaintenanceState != MaintenanceState.Closed;

    private async Task CloseSelectedBlockAsync()
    {
        var block = SelectedBlock;
        if (block is null)
        {
            return;
        }

        CommunicationStatus = $"Sending maintenance request for Block {block.BlockId}...";

        try
        {
            await _ctc.CloseBlockAsync(block.BlockId);
            CommunicationStatus = $"Maintenance request sent for Block {block.BlockId}.";
        }
        catch (Exception ex)
        {
            // MessageSendException carries a dispatcher-readable reason (e.g. "Track
            // Controller is not connected."). Everything is caught because this runs
            // from an async void command and must never crash the UI.
            CommunicationStatus = $"Unable to send maintenance request: {ex.Message}";
        }
        finally
        {
            block.Refresh();
        }
    }
}
