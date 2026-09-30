namespace CTC.Core.Models;

/// <summary>
/// CTC's view of one transit line.
/// </summary>
public class CtcLineState
{
    public string LineId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int TicketSalesPerHour { get; set; }

    public IList<CtcBlockState> Blocks { get; } = new List<CtcBlockState>();
}
