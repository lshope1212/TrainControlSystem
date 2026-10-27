using System.Globalization;
using System.Windows.Input;
using CTC.TestUI.Wpf.Commands;
using CTC.TestUI.Wpf.Services;
using TrainControl.Contracts.Messages;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Simulates the Track Model -> CTC ticket sales input by sending a real
/// <see cref="TicketSalesMessage"/> to the running CTC. The line must already exist in CTC
/// (send the sample layout first); CTC rejects ticket sales for unknown lines.
/// </summary>
public class TicketSalesInputViewModel : CtcInputViewModelBase
{
    private LineOption _selectedLine;
    private string _ticketsPerHourText = "0";

    public TicketSalesInputViewModel(ICtcMessageSender sender, CommunicationStatusViewModel communicationStatus)
        : base(sender, communicationStatus)
    {
        _selectedLine = LineOptions[0];
        SendCommand = new AsyncRelayCommand(_ => SendAsync(), _ => TryGetTicketsPerHour(out var _));
    }

    /// <summary>Lines ticket sales can be sent for. IDs match <see cref="SampleData.SampleTrackLayout"/>.</summary>
    public IReadOnlyList<LineOption> LineOptions { get; } =
    [
        new LineOption("BLUE", "Blue Line"),
    ];

    public LineOption SelectedLine
    {
        get => _selectedLine;
        set => SetProperty(ref _selectedLine, value);
    }

    /// <summary>User-entered tickets sold per hour; must be a whole number of zero or more to send.</summary>
    public string TicketsPerHourText
    {
        get => _ticketsPerHourText;
        set => SetProperty(ref _ticketsPerHourText, value);
    }

    public ICommand SendCommand { get; }

    private bool TryGetTicketsPerHour(out int ticketsPerHour) =>
        int.TryParse(TicketsPerHourText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ticketsPerHour);

    private Task SendAsync()
    {
        if (!TryGetTicketsPerHour(out var ticketsPerHour))
        {
            Result = "Enter tickets sold per hour as a whole number of zero or more.";
            return Task.CompletedTask;
        }

        // The SAME contract object the real Track Model will eventually send to CTC.
        var message = new TicketSalesMessage
        {
            LineId = SelectedLine.LineId,
            TicketsPerHour = ticketsPerHour,
        };

        return SendToCtcAsync(message, $" ({SelectedLine.DisplayName}: {ticketsPerHour} tickets/hour)");
    }

    /// <summary>A line the user can pick, shown by name but sent by ID.</summary>
    public sealed record LineOption(string LineId, string DisplayName);
}
