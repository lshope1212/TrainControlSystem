namespace TrainControl.Contracts.Messages;

/// <summary>
/// Ticket sales throughput for a line.
/// Direction: Track Model -> CTC.
/// </summary>
public class TicketSalesMessage
{
    public string LineId { get; set; } = string.Empty;

    public int TicketsPerHour { get; set; }
}
