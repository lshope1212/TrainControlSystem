using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TrackModel.Core.Models;

namespace TrackModel.Core.Persistence;

/// <summary>
/// Reads the course workbook format (Track Layout &amp; Vehicle Data vF5): every one of its
/// "Blue Line", "Red Line" and "Green Line" worksheets that is present, into one layout.
/// No Excel installation or macros required.
/// </summary>
/// <remarks>
/// <para>
/// Block identity. Blue keeps its established IDs <c>1</c>–<c>15</c>; Red and Green blocks are
/// <c>RED-n</c> and <c>GREEN-n</c>, so IDs are unique across the network (block-level messages
/// carry no line). The section letter and block number stay in their own fields.
/// </para>
/// <para>
/// Red/Green conversion rules (the worksheets list blocks and equipment, not connections):
/// </para>
/// <list type="bullet">
///   <item>A switch written <c>SWITCH (a-b; c-d)</c> joins the block common to both pairs (the
///   switch block) to the other end of the first pair (Normal) or of the second pair (Reverse).
///   The workbook sometimes writes it on the row next to that block (Red 15/16, 32/33, 43/44;
///   Green 12/13, 76/77); the switch is modelled on the common block.</item>
///   <item>Consecutive block numbers are connected, except where both are the far ends of switch
///   branches (Red 66-67 and 71-72, Green 100-101): those blocks join their switches instead.</item>
///   <item>Yard switches (Red 9, Green 58 and 62) are not modelled: the yard is not a block in the
///   workbook, so those blocks stay plain main-line blocks.</item>
///   <item>The worksheets have no signal ("Light") or transponder entries for Red and Green, so
///   none are modelled; station side and "UNDERGROUND" are not part of this model.</item>
///   <item>Direction of travel is taken from the workbook's Track Layout drawing: Red is
///   bidirectional throughout; on Green, A-C (1-12) runs toward block 1, D-F (13-28) and N (77-85)
///   are bidirectional, and the rest runs in increasing block order.</item>
/// </list>
/// <para>The result is checked against the vF5 switch layout and block counts; a workbook that
/// does not match is rejected rather than guessed at.</para>
/// </remarks>
internal static class CourseWorkbookReader
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly Regex SwitchLegs = new(@"SWITCH\s*\(\s*(\d+)\s*-\s*(\d+)\s*;\s*(\d+)\s*-\s*(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Course data for a Red or Green worksheet: expected size, ID prefix, and the vF5 switches (switch, Normal, Reverse).</summary>
    private sealed record LineSheet(string Sheet, string LineId, string IdPrefix, int Blocks, (int Switch, int Normal, int Reverse)[] Switches, int[] YardSwitchRows, Func<int, string> Direction);

    private static readonly LineSheet Red = new("Red Line", "Red", "RED-", 76,
        [(16, 15, 1), (27, 28, 76), (33, 32, 72), (38, 39, 71), (44, 43, 67), (52, 53, 66)], [9], _ => "Bidirectional");

    private static readonly LineSheet Green = new("Green Line", "Green", "GREEN-", 150,
        [(13, 12, 1), (28, 29, 150), (77, 76, 101), (85, 86, 100)], [58, 62],
        n => n <= 12 ? "Reverse" : n <= 28 ? "Bidirectional" : n <= 76 ? "Forward" : n <= 85 ? "Bidirectional" : "Forward");

    public static TrackLayout Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var workbook = new Workbook(zip);
        var names = new List<string>();
        var blocks = new List<TrackBlock>();
        if (workbook.Rows("Blue Line") is { } blue)
        {
            blocks.AddRange(ReadBlue(blue));
            names.Add("Blue");
        }

        foreach (var line in new[] { Red, Green })
        {
            if (workbook.Rows(line.Sheet) is { } rows)
            {
                blocks.AddRange(ReadRedOrGreen(rows, line));
                names.Add(line.LineId);
            }
        }

        if (names.Count == 0)
        {
            throw new InvalidDataException("Workbook needs a 'Blue Line', 'Red Line' or 'Green Line' worksheet (course vF5 format).");
        }

        return new TrackLayout
        {
            Name = names.Count == 1 ? $"Course {names[0]} Line (vF5)" : $"Course {string.Join(", ", names.SkipLast(1))} and {names[^1]} Lines (vF5)",
            Blocks = blocks
        };
    }

    private static List<TrackBlock> ReadBlue(List<Dictionary<string, string>> rows)
    {
        CheckHeader(rows, "Blue Line");
        var blocks = new List<TrackBlock>();
        var elevation = 0d;
        for (int i = 1; i < rows.Count; i++)
        {
            var cells = rows[i];
            // Rows 2–16 are the data; later rows belong to the embedded schematic.
            if (RowNumber(cells) > 16) break;
            var number = Number(cells, "C", "Blue Line");
            if (number != Math.Truncate(number) || number < 1 || number > 15) throw new InvalidDataException("Blue Line needs blocks 1–15.");
            var length = Number(cells, "D", "Blue Line"); var grade = Number(cells, "E", "Blue Line");
            elevation += grade * length / 100; // Equivalent to workbook I/J formulas, independent of cached formula values.
            var infrastructure = cells.GetValueOrDefault("G", "").Trim();
            var station = infrastructure.StartsWith("Station ", StringComparison.OrdinalIgnoreCase) ? infrastructure : "";
            blocks.Add(new TrackBlock
            {
                Id = ((int)number).ToString(CultureInfo.InvariantCulture), Number = (int)number, LineId = "Blue",
                Section = cells.GetValueOrDefault("B", ""), LengthMeters = length, GradePercent = grade, ElevationMeters = elevation,
                SpeedLimitMetersPerSecond = Number(cells, "F", "Blue Line") / 3.6, StationName = station,
                HasCrossing = infrastructure.Contains("RAILWAY CROSSING", StringComparison.OrdinalIgnoreCase),
                HasSwitch = infrastructure.Contains(" or ", StringComparison.OrdinalIgnoreCase),
                HasSignal = infrastructure.Contains("Light", StringComparison.OrdinalIgnoreCase),
                Beacon = infrastructure.Equals("Transponder", StringComparison.OrdinalIgnoreCase) ? "pending" : "",
                InitialWaitingPassengers = station.Length > 0 ? 24 : 0,
                // Workbook has no direction/heater columns: documented simulation configuration.
                TravelDirection = "Bidirectional", HasHeater = true
            });
        }
        if (blocks.Count != 15 || !blocks.Select(b => b.Number).Order().SequenceEqual(Enumerable.Range(1, 15)))
            throw new InvalidDataException("Course Blue Line must contain each of blocks 1–15 exactly once.");
        TrackBlock Block(int n) => blocks.Single(b => b.Number == n);
        void Connect(int a, int b) { Block(a).ConnectedBlockIds.Add(Block(b).Id); Block(b).ConnectedBlockIds.Add(Block(a).Id); }
        for (var n = 1; n < 5; n++) Connect(n, n + 1);
        Connect(5, 6); Connect(5, 11);
        for (var n = 6; n < 10; n++) Connect(n, n + 1);
        for (var n = 11; n < 15; n++) Connect(n, n + 1);
        if (!Block(5).HasSwitch || !Block(6).HasSignal || !Block(11).HasSignal || !Block(3).HasCrossing
            || Block(9).Beacon.Length == 0 || Block(14).Beacon.Length == 0
            || Block(10).StationName.Length == 0 || Block(15).StationName.Length == 0)
            throw new InvalidDataException("Blue Line infrastructure does not match the course vF5 topology.");
        Block(5).NormalNextBlockId = "6"; Block(5).ReverseNextBlockId = "11";
        foreach (var n in new[] { 9, 14 }) { Block(n).Beacon = Block(n + 1).StationName; Block(n).BeaconTargetBlockId = Block(n + 1).Id; }
        return blocks;
    }

    private static List<TrackBlock> ReadRedOrGreen(List<Dictionary<string, string>> rows, LineSheet line)
    {
        CheckHeader(rows, line.Sheet);
        var byNumber = new SortedDictionary<int, TrackBlock>();
        var switches = new List<(int Row, int Switch, int Normal, int Reverse)>();
        var yardRows = new List<int>();
        var elevation = 0d;
        for (int i = 1; i < rows.Count; i++)
        {
            var cells = rows[i];
            // Data rows name their line in column A; the rows after them are notes and drawings.
            if (!string.Equals(cells.GetValueOrDefault("A", "").Trim(), line.LineId, StringComparison.OrdinalIgnoreCase)) break;
            var value = Number(cells, "C", line.Sheet);
            if (value != Math.Truncate(value) || value < 1 || value > line.Blocks || byNumber.ContainsKey((int)value))
                throw new InvalidDataException($"{line.Sheet} needs each of blocks 1–{line.Blocks} once (row {RowNumber(cells)}).");
            int number = (int)value;
            var length = Number(cells, "D", line.Sheet); var grade = Number(cells, "E", line.Sheet);
            if (length <= 0) throw new InvalidDataException($"{line.Sheet} block {number} needs a positive length.");
            elevation += grade * length / 100; // Same as the workbook's cumulative elevation formulas.
            var infrastructure = cells.GetValueOrDefault("G", "").Trim();
            if (infrastructure.Contains("SWITCH", StringComparison.OrdinalIgnoreCase))
            {
                if (infrastructure.Contains("YARD", StringComparison.OrdinalIgnoreCase)) yardRows.Add(number);
                else if (SwitchLegs.Match(infrastructure) is { Success: true } legs)
                {
                    int[] pair1 = [int.Parse(legs.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(legs.Groups[2].Value, CultureInfo.InvariantCulture)];
                    int[] pair2 = [int.Parse(legs.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(legs.Groups[4].Value, CultureInfo.InvariantCulture)];
                    var common = pair1.Intersect(pair2).ToArray();
                    if (common.Length != 1 || pair1[0] == pair1[1] || pair2[0] == pair2[1])
                        throw new InvalidDataException($"{line.Sheet} block {number}: cannot read the switch legs in '{infrastructure}'.");
                    switches.Add((number, common[0], pair1.Single(n => n != common[0]), pair2.Single(n => n != common[0])));
                }
                else throw new InvalidDataException($"{line.Sheet} block {number}: unrecognized switch '{infrastructure}'.");
            }

            byNumber[number] = new TrackBlock
            {
                Id = line.IdPrefix + number.ToString(CultureInfo.InvariantCulture), Number = number, LineId = line.LineId,
                Section = cells.GetValueOrDefault("B", "").Trim(), LengthMeters = length, GradePercent = grade, ElevationMeters = elevation,
                SpeedLimitMetersPerSecond = Number(cells, "F", line.Sheet) / 3.6,
                StationName = StationName(infrastructure, line.LineId, number),
                HasCrossing = infrastructure.Contains("RAILWAY CROSSING", StringComparison.OrdinalIgnoreCase),
                // Workbook has no heater column: same documented simulation configuration as the Blue Line.
                HasHeater = true, TravelDirection = line.Direction(number)
            };
            byNumber[number].InitialWaitingPassengers = byNumber[number].StationName.Length > 0 ? 24 : 0;
        }

        if (byNumber.Count != line.Blocks)
            throw new InvalidDataException($"{line.Sheet} must contain each of blocks 1–{line.Blocks} exactly once.");
        var found = switches.Select(s => (s.Switch, s.Normal, s.Reverse)).Order().ToArray();
        if (!found.SequenceEqual(line.Switches.Order()) || !yardRows.Order().SequenceEqual(line.YardSwitchRows))
            throw new InvalidDataException($"{line.Sheet} switches do not match the course vF5 topology.");

        TrackBlock Block(int n) => byNumber[n];
        void Connect(int a, int b)
        {
            if (Block(a).ConnectedBlockIds.Contains(Block(b).Id)) return;
            Block(a).ConnectedBlockIds.Add(Block(b).Id); Block(b).ConnectedBlockIds.Add(Block(a).Id);
        }
        // Far ends of switch branches (not numbered next to their switch block) join that switch, not each other.
        var branchEnds = switches.SelectMany(s => new[] { s.Normal, s.Reverse }.Where(end => Math.Abs(end - s.Switch) > 1)).ToHashSet();
        for (var n = 1; n < line.Blocks; n++)
            if (!(branchEnds.Contains(n) && branchEnds.Contains(n + 1))) Connect(n, n + 1);
        foreach (var s in switches)
        {
            Connect(s.Switch, s.Normal); Connect(s.Switch, s.Reverse);
            var block = Block(s.Switch);
            block.HasSwitch = true;
            block.NormalNextBlockId = Block(s.Normal).Id;
            block.ReverseNextBlockId = Block(s.Reverse).Id;
        }

        foreach (var block in byNumber.Values)
        {
            if (block.ConnectedBlockIds.Count > (block.HasSwitch ? 3 : 2) || block.ConnectedBlockIds.Count == 0)
                throw new InvalidDataException($"{line.Sheet} block {block.Number} has {block.ConnectedBlockIds.Count} connections; check its switches.");
        }

        return [.. byNumber.Values];
    }

    /// <summary>"STATION; PENN STATION; UNDERGROUND" -> "Penn Station". A station row without a name is named after its block, visibly.</summary>
    private static string StationName(string infrastructure, string lineId, int number)
    {
        var parts = infrastructure.Split([';', ':'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("STATION", StringComparison.OrdinalIgnoreCase)) return "";
        var name = string.Join(" ", parts.Skip(1).Where(part => !part.StartsWith("UNDER", StringComparison.OrdinalIgnoreCase)));
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return name.Length == 0 ? $"{lineId} {number} (unnamed station)" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
    }

    private static void CheckHeader(List<Dictionary<string, string>> rows, string sheet)
    {
        var header = rows.FirstOrDefault() ?? throw new InvalidDataException($"'{sheet}' worksheet is empty.");
        foreach (var pair in new[] { ("C", "Block Number"), ("D", "Block Length (m)"), ("E", "Block Grade (%)"), ("F", "Speed Limit (Km/Hr)"), ("G", "Infrastructure") })
            if (header.GetValueOrDefault(pair.Item1)?.Trim() != pair.Item2) throw new InvalidDataException($"Unsupported {sheet} column: " + pair.Item2);
    }

    private static int RowNumber(Dictionary<string, string> cells) => int.Parse(cells["#row"], CultureInfo.InvariantCulture);

    private static double Number(Dictionary<string, string> cells, string column, string sheet) =>
        double.TryParse(cells.GetValueOrDefault(column), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
            ? n : throw new InvalidDataException($"{sheet} row {cells["#row"]} needs numeric column {column}.");

    /// <summary>Worksheet rows as column-letter -> text, plus the row number under "#row".</summary>
    private sealed class Workbook(ZipArchive zip)
    {
        private readonly string[] _strings = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Read(zip, "xl/sharedStrings.xml")
            .Descendants(S + "si").Select(x => string.Concat(x.Descendants(S + "t").Select(t => t.Value))).ToArray();
        private readonly XDocument _workbook = Read(zip, "xl/workbook.xml");

        public List<Dictionary<string, string>>? Rows(string sheetName)
        {
            var sheet = _workbook.Descendants(S + "sheet").FirstOrDefault(x => (string?)x.Attribute("name") == sheetName);
            if (sheet is null) return null;
            var relationship = Read(zip, "xl/_rels/workbook.xml.rels").Root!.Elements()
                .Single(x => (string?)x.Attribute("Id") == (string?)sheet.Attribute(R + "id"));
            var target = (string)relationship.Attribute("Target")!;
            var entryName = new Uri(new Uri("https://workbook.local/xl/workbook.xml"), target).AbsolutePath.TrimStart('/');
            return Read(zip, entryName).Descendants(S + "row").Select(row =>
            {
                var cells = row.Elements(S + "c").ToDictionary(c => Regex.Replace((string)c.Attribute("r")!, "[0-9]", ""), Value);
                cells["#row"] = (string?)row.Attribute("r") ?? "0";
                return cells;
            }).ToList();
        }

        private string Value(XElement cell)
        {
            var raw = cell.Element(S + "v")?.Value ?? "";
            return (string?)cell.Attribute("t") switch
            {
                "s" => _strings[int.Parse(raw, CultureInfo.InvariantCulture)],
                "inlineStr" => string.Concat(cell.Descendants(S + "t").Select(t => t.Value)),
                _ => raw
            };
        }

        private static XDocument Read(ZipArchive zip, string entry)
        {
            using var stream = zip.GetEntry(entry)?.Open() ?? throw new InvalidDataException("Workbook is missing " + entry);
            return XDocument.Load(stream);
        }
    }
}
