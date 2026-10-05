using System.Windows.Media;
using TrackModel.Core.Interfaces;
using TrackModel.Core.Models;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackModel.Wpf.ViewModels;

/// <summary>Presentation adapter; failure changes are applied through the domain service.</summary>
public sealed class BlockViewModel : ViewModelBase
{
    private readonly TrackBlock _block;
    private readonly ITrackService _track;
    public BlockViewModel(TrackBlock block, ITrackService track) { _block = block; _track = track; }
    public string Id => _block.Id;
    public int Number => _block.Number;
    public string LineId => _block.LineId;
    public string Section => _block.Section;
    public IReadOnlyList<string> Connections => _block.ConnectedBlockIds;
    public string Station => _block.StationName;
    public bool HasSwitch => _block.HasSwitch;
    public bool HasCrossing => _block.HasCrossing;
    public bool HasSignal => _block.HasSignal;
    public bool IsOccupied => _block.IsOccupied;
    public bool IsClosed => _block.IsClosed;
    public bool HasFailure => _block.HasFailure;
    public string Occupancy => _block.IsOccupied ? "Occupied" : "Clear";
    public string ReportedOccupancy => _block.ReportedOccupancy.ToString();
    public string Length => $"{_block.LengthMeters / 0.3048:N0} ft";
    public string Elevation => $"{_block.ElevationMeters / 0.3048:N0} ft";
    public string Grade => $"{_block.GradePercent:0.##}%";
    public string Temperature => $"{_block.TemperatureCelsius * 1.8 + 32:0.#} °F";
    public string Signal => !HasSignal ? "No signal" : _block.EffectiveSignal switch
        { SignalState.Green => "Proceed", SignalState.Yellow => "Caution", SignalState.Red => "Stop", _ => "Unknown" };
    public SignalState SignalState => _block.EffectiveSignal;
    public string Switch => HasSwitch ? _block.Switch.ToString() : "—";
    public string Crossing => HasCrossing ? (_block.Crossing == CrossingState.Closed ? "Gates down" : "Gates up") : "—";
    public string NextBlock => _block.NextBlockId.Length > 0 ? _block.NextBlockId : "—";
    public string Train => _block.TrainId.Length > 0 ? _block.TrainId : "—";
    public string ActualSpeed => $"{_block.ActualSpeedMetersPerSecond / 0.44704:0.#} mph";
    public string CommandedSpeed => $"{_block.CommandedSpeedMetersPerSecond / 0.44704:0.#} mph";
    public string Authority => $"{_block.AuthorityMeters / 0.3048:N0} ft";
    public string Maintenance => IsClosed ? "Closed" : "Open";
    public int Boarding => _block.BoardingPassengers;
    public int Disembarking => _block.DisembarkingPassengers;
    public int TicketsSold => _block.TicketsSold;
    public int Waiting => _block.WaitingPassengers;
    public Brush StateBrush => HasFailure ? Brushes.DarkOrange : IsClosed ? Brushes.SlateGray : IsOccupied ? Brushes.IndianRed : Brushes.ForestGreen;
    public string FailureSummary => string.Join(", ", new[]
        { _block.BrokenRail ? "Broken rail" : null, _block.TrackCircuitFailure ? "Track circuit" : null, _block.PowerFailure ? "Power" : null }
        .Where(x => x is not null));
    public bool BrokenRail { get => _block.BrokenRail; set => SetFailures(value, TrackCircuitFailure, PowerFailure); }
    public bool TrackCircuitFailure { get => _block.TrackCircuitFailure; set => SetFailures(BrokenRail, value, PowerFailure); }
    public bool PowerFailure { get => _block.PowerFailure; set => SetFailures(BrokenRail, TrackCircuitFailure, value); }
    private void SetFailures(bool rail, bool circuit, bool power) =>
        _track.ApplyFailures(new TrackModelFailureCommandMessage { BlockId = Id, BrokenRail = rail, TrackCircuitFailure = circuit, PowerFailure = power });
    public void Refresh() => OnPropertyChanged(string.Empty);
}
