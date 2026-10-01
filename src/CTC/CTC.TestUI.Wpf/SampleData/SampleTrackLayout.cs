using TrainControl.Contracts.Messages;

namespace CTC.TestUI.Wpf.SampleData;

/// <summary>
/// Small, hand-made layout for exercising CTC.Core in the Test UI, expressed with the
/// real shared contracts. Illustrative only; it is NOT the real Green/Red line data
/// that the Track Model will eventually provide.
/// </summary>
public static class SampleTrackLayout
{
    public static TrackLayoutMessage Create() => new TrackLayoutMessage
    {
        Lines =
        {
            new TrackLineDefinition
            {
                LineId = "GREEN",
                Name = "Green Line (sample)",
                Blocks =
                {
                    Block("G1", 1, "A", 100, connectedTo: ["G2"], hasSignal: true),
                    Block("G2", 2, "A", 100, connectedTo: ["G1", "G3"], hasCrossing: true),
                    Block("G3", 3, "B", 150, connectedTo: ["G2", "G4"], station: "Pioneer"),
                    Block("G4", 4, "B", 100, connectedTo: ["G3", "G5", "G6"], hasSwitch: true, hasSignal: true),
                    Block("G5", 5, "C", 120, connectedTo: ["G4"], station: "Edgebrook"),
                    Block("G6", 6, "D", 120, connectedTo: ["G4"]),
                },
            },
            new TrackLineDefinition
            {
                LineId = "RED",
                Name = "Red Line (sample)",
                Blocks =
                {
                    Block("R1", 1, "A", 80, connectedTo: ["R2"], hasSignal: true),
                    Block("R2", 2, "A", 80, connectedTo: ["R1", "R3"], station: "Shadyside"),
                    Block("R3", 3, "B", 90, connectedTo: ["R2"]),
                },
            },
        },
    };

    private static TrackBlockDefinition Block(
        string blockId,
        int number,
        string section,
        double lengthMeters,
        string[] connectedTo,
        string station = "",
        bool hasSwitch = false,
        bool hasSignal = false,
        bool hasCrossing = false)
    {
        var block = new TrackBlockDefinition
        {
            BlockId = blockId,
            BlockNumber = number,
            Section = section,
            LengthMeters = lengthMeters,
            StationName = station,
            HasSwitch = hasSwitch,
            HasSignal = hasSignal,
            HasCrossing = hasCrossing,
        };
        block.ConnectedBlockIds.AddRange(connectedTo);
        return block;
    }
}
