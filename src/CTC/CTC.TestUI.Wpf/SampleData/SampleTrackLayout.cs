using TrainControl.Contracts.Messages;

namespace CTC.TestUI.Wpf.SampleData;

/// <summary>
/// Small, hand-made layout the Test UI sends to the running CTC, expressed with the
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
                LineId = "BLUE",
                Name = "Blue Line",
                Blocks =
                {
                    Block("B1",  1, "A", 50, connectedTo: ["B2"]),
                    Block("B2",  2, "A", 50, connectedTo: ["B1", "B3"]),
                    Block("B3",  3, "A", 50, connectedTo: ["B2", "B4"],
                        hasCrossing: true),

                    Block("B4",  4, "A", 50, connectedTo: ["B3", "B5"]),

                    Block("B5",  5, "A", 50,
                        connectedTo: ["B4", "B6", "B11"],
                        hasSwitch: true),

                    Block("B6",  6, "B", 50,
                        connectedTo: ["B5", "B7"],
                        hasSignal: true),

                    Block("B7",  7, "B", 50, connectedTo: ["B6", "B8"]),
                    Block("B8",  8, "B", 50, connectedTo: ["B7", "B9"]),
                    Block("B9",  9, "B", 50, connectedTo: ["B8", "B10"]),

                    Block("B10", 10, "B", 50,
                        connectedTo: ["B9"],
                        station: "Station B"),

                    Block("B11", 11, "C", 50,
                        connectedTo: ["B5", "B12"],
                        hasSignal: true),

                    Block("B12", 12, "C", 50, connectedTo: ["B11", "B13"]),
                    Block("B13", 13, "C", 50, connectedTo: ["B12", "B14"]),
                    Block("B14", 14, "C", 50, connectedTo: ["B13", "B15"]),

                    Block("B15", 15, "C", 50,
                        connectedTo: ["B14"],
                        station: "Station C"),
                }
            }
        }
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
