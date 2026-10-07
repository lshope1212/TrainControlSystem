using TrackModel.Core.Models;
namespace TrackModel.Core.Persistence;

public static class TrackLayoutValidator
{
    public static void Validate(TrackLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (string.IsNullOrWhiteSpace(layout.Name) || layout.Blocks is null || layout.Blocks.Count == 0)
            throw new ArgumentException("A layout needs a name and at least one block.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in layout.Blocks)
        {
            if (b is null || string.IsNullOrWhiteSpace(b.Id) || string.IsNullOrWhiteSpace(b.LineId) || !ids.Add(b.Id))
                throw new ArgumentException("Block IDs must be nonempty and unique across all lines; each block needs a LineId.");
            if (string.IsNullOrWhiteSpace(b.Section) || b.StationName is null
                || b.NormalNextBlockId is null || b.ReverseNextBlockId is null || b.Number < 0
                || b.TravelDirection is not ("Forward" or "Reverse" or "Bidirectional"))
                throw new ArgumentException($"Block {b.Id} has invalid section, station, or route metadata.");
            if (!double.IsFinite(b.LengthMeters) || b.LengthMeters <= 0
                || !double.IsFinite(b.SpeedLimitMetersPerSecond) || b.SpeedLimitMetersPerSecond < 0
                || !double.IsFinite(b.ElevationMeters) || !double.IsFinite(b.GradePercent)
                || !double.IsFinite(b.TemperatureCelsius) || b.TemperatureCelsius < -273.15
                || b.InitialWaitingPassengers < 0 || b.ConnectedBlockIds is null)
                throw new ArgumentException($"Block {b.Id} has invalid physical values or connections.");
            if (b.InitialWaitingPassengers > 0 && string.IsNullOrWhiteSpace(b.StationName))
                throw new ArgumentException($"Block {b.Id} needs a station for passenger demand.");
        }
        foreach (var b in layout.Blocks)
        {
            foreach (var id in b.ConnectedBlockIds)
                if (!ids.Contains(id) || id == b.Id || layout.Blocks.First(x => x.Id == id).LineId != b.LineId)
                    throw new ArgumentException($"Block {b.Id} has an invalid connection '{id}'.");
            if (b.HasSwitch && (b.NormalNextBlockId == b.ReverseNextBlockId
                || !b.ConnectedBlockIds.Contains(b.NormalNextBlockId) || !b.ConnectedBlockIds.Contains(b.ReverseNextBlockId)))
                throw new ArgumentException($"Switch {b.Id} needs two distinct connected Normal/Reverse destinations.");
        }
    }
}
