using System.Windows;

namespace CTC.Wpf.Layout;

/// <summary>
/// A section's schematic path. Blocks of the section are spread along it in increasing
/// block-number order, so the first point is the end nearest the section's lowest block.
/// </summary>
/// <param name="LabelSide">+1: block labels on the left of the path direction; -1: on the right.</param>
/// <param name="BadgeSide">Side for station names and status badges, in the same terms; null
/// means opposite the labels. On the same side they are stacked outward after the label.</param>
public sealed record TerritorySectionPath(string Section, IReadOnlyList<Point> Points, int LabelSide = 1, int? BadgeSide = null);

/// <summary>A visual-only terminal such as the yard, joined to the given block numbers.</summary>
public sealed record TerritoryTerminalDefinition(string Label, Point Position, IReadOnlyList<int> BlockNumbers);

/// <summary>
/// Presentation-only schematic of one line. Coordinates are in the units of the reference
/// drawing they were traced from and are multiplied by <see cref="ScaleX"/>/<see cref="ScaleY"/>.
/// </summary>
public sealed record TerritoryLineSchematic(
    string Key,
    double ScaleX,
    double ScaleY,
    IReadOnlyList<TerritorySectionPath> Sections,
    IReadOnlyList<TerritoryTerminalDefinition> Terminals)
{
    public TerritorySectionPath? FindSection(string section) =>
        Sections.FirstOrDefault(path => string.Equals(path.Section, section, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Section-level schematics for the course lines, traced from the "Track Layout" drawings of
/// the course workbook (Track Layout &amp; Vehicle Data vF5). The drawings are not to scale and
/// neither is this; only where to draw each section is defined here. What actually connects
/// to what always comes from the loaded blocks' ConnectedBlockIds.
/// </summary>
public static class TerritoryLayoutProvider
{
    private static Point P(double x, double y) => new Point(x, y);

    private static TerritorySectionPath S(string section, int labelSide, params Point[] points) =>
        new TerritorySectionPath(section, points, labelSide);

    /// <summary>A section whose labels and badges share one side (the other side is crowded).</summary>
    private static TerritorySectionPath SameSide(string section, int side, params Point[] points) =>
        new TerritorySectionPath(section, points, side, side);

    /// <summary>Blue Line: yard -> A1..A5, then B6..B10 (Station B) or C11..C15 (Station C).</summary>
    public static TerritoryLineSchematic Blue { get; } = new TerritoryLineSchematic(
        "BLUE", 1.0, 1.0,
        [
            S("A", -1, P(110, 213), P(565, 213)),
            S("B", -1, P(592, 203), P(950, 40)),
            S("C", 1, P(588, 224), P(1100, 400)),
        ],
        [new TerritoryTerminalDefinition("YARD", P(40, 213), [1])]);

    /// <summary>
    /// Red Line: the A-E loop and yard off F16, down H (with the T-S-R and O-P-Q side
    /// tracks), through I to the J-N loop.
    /// </summary>
    public static TerritoryLineSchematic Red { get; } = new TerritoryLineSchematic(
        "RED", 3.0, 2.0,
        [
            S("A", 1, P(348, 165), P(362, 148), P(378, 132)),
            S("B", -1, P(382, 128), P(395, 110), P(415, 100)),
            S("C", -1, P(420, 100), P(450, 110), P(472, 135)),
            S("D", -1, P(474, 142), P(460, 160), P(430, 168), P(405, 168)),
            S("E", -1, P(400, 168), P(351, 168)),
            S("F", -1, P(345, 168), P(268, 168)),
            S("G", 1, P(264, 168), P(240, 175), P(226, 195)),
            // The side tracks join H from the west, so everything is labelled on the east.
            SameSide("H", 1, P(225, 200), P(225, 412)),
            S("I", -1, P(225, 416), P(215, 450), P(190, 475), P(165, 485)),
            SameSide("J", 1, P(160, 485), P(65, 485)),
            S("K", -1, P(60, 480), P(35, 445), P(15, 405)),
            S("L", -1, P(15, 398), P(25, 370), P(55, 358)),
            S("M", -1, P(60, 360), P(68, 400), P(75, 440)),
            S("N", 1, P(77, 446), P(88, 468), P(103, 480)),
            S("O", 1, P(221, 398), P(194, 398)),
            S("P", 1, P(190, 394), P(190, 349)),
            S("Q", 1, P(192, 343), P(221, 340)),
            S("R", 1, P(221, 291), P(194, 284)),
            S("S", 1, P(190, 280), P(190, 246)),
            S("T", -1, P(221, 234), P(194, 240)),
        ],
        [new TerritoryTerminalDefinition("YARD", P(462, 215), [9])]);

    /// <summary>
    /// Green Line: the A-B-C loop at D13, down F/G and H to I, the yard between I and K,
    /// around K-L-M-N (with the O-P-Q loop), back up R-S-T-U-V, then W-X-Y-Z to F28.
    /// </summary>
    public static TerritoryLineSchematic Green { get; } = new TerritoryLineSchematic(
        "GREEN", 3.0, 2.0,
        [
            S("A", 1, P(222, 19), P(250, 45), P(268, 72)),
            S("B", 1, P(272, 76), P(300, 88), P(328, 90)),
            S("C", 1, P(332, 88), P(365, 78), P(380, 58), P(360, 35), P(300, 20), P(226, 13)),
            S("D", 1, P(218, 15), P(150, 17)),
            S("E", 1, P(146, 17), P(115, 18), P(100, 28), P(95, 45), P(95, 70)),
            S("F", 1, P(95, 75), P(95, 165)),
            S("G", 1, P(95, 170), P(95, 218)),
            S("H", 1, P(95, 222), P(100, 250), P(112, 262), P(135, 265)),
            S("I", -1, P(140, 265), P(355, 265)),
            S("J", -1, P(362, 268), P(400, 280), P(440, 300), P(455, 318)),
            S("K", -1, P(455, 322), P(455, 505)),
            S("L", -1, P(455, 510), P(450, 560), P(430, 592), P(412, 605)),
            S("M", -1, P(408, 605), P(262, 605)),
            S("N", -1, P(255, 605), P(147, 605)),
            SameSide("O", 1, P(142, 605), P(75, 605)),
            S("P", -1, P(70, 603), P(48, 580), P(42, 545), P(52, 518), P(72, 510), P(92, 522), P(100, 550), P(102, 570)),
            S("Q", 1, P(104, 575), P(118, 592), P(140, 600)),
            S("R", 1, P(260, 600), P(285, 572)),
            S("S", -1, P(290, 570), P(345, 572)),
            S("T", 1, P(350, 572), P(390, 560), P(418, 530), P(430, 490)),
            S("U", -1, P(430, 485), P(430, 380)),
            S("V", 1, P(430, 375), P(425, 345), P(400, 315), P(365, 300), P(335, 298)),
            S("W", -1, P(330, 298), P(115, 298)),
            S("X", -1, P(110, 298), P(85, 292), P(68, 272)),
            S("Y", 1, P(65, 265), P(65, 205)),
            S("Z", 1, P(68, 198), P(92, 172)),
        ],
        [new TerritoryTerminalDefinition("YARD", P(440, 225), [57, 63])]);

    private static readonly TerritoryLineSchematic[] Schematics = [Blue, Red, Green];

    /// <summary>
    /// Finds the schematic for a line by its ID or name (e.g. "GREEN", "Green Line");
    /// null for a line with no custom geometry, which then uses the generic fallback.
    /// </summary>
    public static TerritoryLineSchematic? Find(string? lineIdOrName)
    {
        if (string.IsNullOrWhiteSpace(lineIdOrName))
        {
            return null;
        }

        string key = lineIdOrName.Trim().ToUpperInvariant();
        return Schematics.FirstOrDefault(schematic => key == schematic.Key)
            ?? Schematics.FirstOrDefault(schematic => key.Split(' ', '-', '_').Contains(schematic.Key));
    }
}
