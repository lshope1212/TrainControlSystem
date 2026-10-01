using System.Windows.Input;
using CTC.Core.Interfaces;
using CTC.TestUI.Wpf.Commands;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Dispatcher action: close a block for maintenance via the real CTCService.CloseBlockAsync.
/// The block ID is free text and is deliberately NOT validated here, so CTC.Core's own
/// validation (blank/unknown block) can be exercised from the harness.
/// </summary>
public class BlockMaintenanceActionViewModel : ViewModelBase
{
    private readonly ICTCService _ctc;
    private readonly Action _onCtcStateChanged;
    private string _blockId = string.Empty;
    private string _result = string.Empty;

    public BlockMaintenanceActionViewModel(ICTCService ctc, Action onCtcStateChanged)
    {
        _ctc = ctc;
        _onCtcStateChanged = onCtcStateChanged;
        CloseBlockCommand = new AsyncRelayCommand(_ => CloseBlockAsync());
    }

    public string BlockId
    {
        get => _blockId;
        set => SetProperty(ref _blockId, value);
    }

    public ICommand CloseBlockCommand { get; }

    public string Result
    {
        get => _result;
        private set => SetProperty(ref _result, value);
    }

    private async Task CloseBlockAsync()
    {
        var blockId = BlockId;

        try
        {
            await _ctc.CloseBlockAsync(blockId);
            Result = $"CloseBlockAsync(\"{blockId}\") completed.";
        }
        catch (Exception ex)
        {
            // Runs from an async void command: report, never crash the harness.
            Result = $"CloseBlockAsync(\"{blockId}\") failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _onCtcStateChanged();
        }
    }
}
