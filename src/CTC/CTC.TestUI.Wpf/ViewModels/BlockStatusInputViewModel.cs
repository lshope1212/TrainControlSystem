using CTC.Core.Interfaces;
using CTC.TestUI.Wpf.Commands;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Input;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.TestUI.Wpf.ViewModels
{
    public class BlockStatusInputViewModel : ViewModelBase
    {
        private readonly ICTCService _ctc;
        private readonly Action _onCtcStateChanged;
        private string _blockId = string.Empty;
        private OccupancyState _selectedOccupancy = OccupancyState.Unknown;
        private SignalState _selectedSignal = SignalState.Unknown;
        private SwitchPosition _selectedSwitch = SwitchPosition.Unknown;
        private CrossingState _selectedCrossing = CrossingState.Unknown;

        private string _result = string.Empty;

        public BlockStatusInputViewModel(ICTCService ctc, Action onCtcStateChanged)
        {
            _ctc = ctc ?? throw new ArgumentNullException(nameof(ctc));

            _onCtcStateChanged = onCtcStateChanged ?? throw new ArgumentNullException(nameof(onCtcStateChanged));

            //Connect the ApplyCommand to the method that accesses the service
            ApplyCommand = new RelayCommand(_ => Apply());
        }

        // User-entered block
        public string BlockId
        {
            get => _blockId;
            set => SetProperty(ref _blockId, value);
        }

        // Available enum values for the ComboBoxes
        public OccupancyState[] OccupancyOptions { get; } = Enum.GetValues<OccupancyState>();

        public SignalState[] SignalOptions { get; } = Enum.GetValues<SignalState>();

        public SwitchPosition[] SwitchOptions { get; } = Enum.GetValues<SwitchPosition>();

        public CrossingState[] CrossingOptions { get; } = Enum.GetValues<CrossingState>();

        // Selected values from the ComboBoxes
        public OccupancyState SelectedOccupancy
        {
            get => _selectedOccupancy;
            set => SetProperty(ref _selectedOccupancy, value);
        }

        public SignalState SelectedSignal
        {
            get => _selectedSignal;
            set => SetProperty(ref _selectedSignal, value);
        }

        public SwitchPosition SelectedSwitch
        {
            get => _selectedSwitch;
            set => SetProperty(ref _selectedSwitch, value);
        }

        public CrossingState SelectedCrossing
        {
            get => _selectedCrossing;
            set => SetProperty(ref _selectedCrossing, value);
        }

        // Command for the button
        public ICommand ApplyCommand { get; }

        // Status text displayed in the TestUI after the action is applied
        public string Result
        {
            get => _result;
            private set => SetProperty(ref _result, value);
        }

        // Method that connects to the core: apply simulated TrackController input
        private void Apply()
        {
            try
            {
                // Create the SAME contract object that the real Track Controller will eventually send to CTC
                var message = new BlockStatusMessage
                {
                    BlockId = BlockId,
                    Occupancy = SelectedOccupancy,
                    Signal = SelectedSignal,
                    Switch = SelectedSwitch,
                    Crossing = SelectedCrossing
                };

                // Feed it into the real CTC.Core service
                _ctc.ApplyBlockStatus(message);

                Result =
                    $"Block {BlockId}: " +
                    $"Occupancy={SelectedOccupancy}, " +
                    $"Signal={SelectedSignal}, " +
                    $"Switch={SelectedSwitch}, " +
                    $"Crossing={SelectedCrossing}";

                // CtcSystemState itself does not implement WPF notifications, so tell the Test UI's state ViewModel to refresh
                _onCtcStateChanged();
            }
            catch (Exception ex)
            {
                Result = $"ApplyBlockStatus failed: " + $"{ex.GetType().Name}: {ex.Message}";
            }
        }
    }
}
