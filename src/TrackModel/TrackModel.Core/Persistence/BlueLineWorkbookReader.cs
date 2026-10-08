using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TrackModel.Core.Models;

namespace TrackModel.Core.Persistence;

/// <summary>Reads the supplied vF5 course format, Blue Line only. No Excel installation or macros required.</summary>
internal static class BlueLineWorkbookReader
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static TrackLayout Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument Read(string entry)
        {
            using var stream = zip.GetEntry(entry)?.Open() ?? throw new InvalidDataException("Workbook is missing " + entry);
            return XDocument.Load(stream);
        }
        var sheet = Read("xl/workbook.xml").Descendants(S + "sheet").FirstOrDefault(x => (string?)x.Attribute("name") == "Blue Line")
            ?? throw new InvalidDataException("Workbook needs a 'Blue Line' worksheet (course vF5 format).");
        var relationship = Read("xl/_rels/workbook.xml.rels").Root!.Elements()
            .Single(x => (string?)x.Attribute("Id") == (string?)sheet.Attribute(R + "id"));
        var target = (string)relationship.Attribute("Target")!;
        var entryName = new Uri(new Uri("https://workbook.local/xl/workbook.xml"), target).AbsolutePath.TrimStart('/');
        var strings = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Read("xl/sharedStrings.xml")
            .Descendants(S + "si").Select(x => string.Concat(x.Descendants(S + "t").Select(t => t.Value))).ToArray();
        string Value(XElement cell)
        {
            var raw = cell.Element(S + "v")?.Value ?? "";
            return (string?)cell.Attribute("t") switch
            {
                "s" => strings[int.Parse(raw, CultureInfo.InvariantCulture)],
                "inlineStr" => string.Concat(cell.Descendants(S + "t").Select(t => t.Value)),
                _ => raw
            };
        }
        var rows = Read(entryName).Descendants(S + "row").ToList();
        Dictionary<string, string> Cells(XElement row) => row.Elements(S + "c").ToDictionary(
            c => Regex.Replace((string)c.Attribute("r")!, "[0-9]", ""), Value);
        var header = Cells(rows.First());
        foreach (var pair in new[] { ("C", "Block Number"), ("D", "Block Length (m)"), ("E", "Block Grade (%)"), ("F", "Speed Limit (Km/Hr)"), ("G", "Infrastructure") })
            if (header.GetValueOrDefault(pair.Item1)?.Trim() != pair.Item2) throw new InvalidDataException("Unsupported Blue Line column: " + pair.Item2);
        var layout = new TrackLayout { Name = "Course Blue Line (vF5)" };
        var elevation = 0d;
        foreach (var row in rows.Skip(1))
        {
            var cells = Cells(row);
            // Rows 2–16 are the data; later rows belong to the embedded schematic.
            if ((int?)row.Attribute("r") > 16) break;
            double Number(string column) => double.TryParse(cells.GetValueOrDefault(column), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && double.IsFinite(n) ? n : throw new InvalidDataException($"Blue Line row {(string?)row.Attribute("r")} needs numeric column {column}.");
            var number = Number("C");
            if (number != Math.Truncate(number) || number < 1 || number > 15) throw new InvalidDataException("Blue Line needs blocks 1–15.");
            var length = Number("D"); var grade = Number("E");
            elevation += grade * length / 100; // Equivalent to workbook I/J formulas, independent of cached formula values.
            var infrastructure = cells.GetValueOrDefault("G", "").Trim();
            var station = infrastructure.StartsWith("Station ", StringComparison.OrdinalIgnoreCase) ? infrastructure : "";
            layout.Blocks.Add(new TrackBlock
            {
                Id = ((int)number).ToString(CultureInfo.InvariantCulture), Number = (int)number, LineId = "Blue",
                Section = cells.GetValueOrDefault("B", ""), LengthMeters = length, GradePercent = grade, ElevationMeters = elevation,
                SpeedLimitMetersPerSecond = Number("F") / 3.6, StationName = station,
                HasCrossing = infrastructure.Contains("RAILWAY CROSSING", StringComparison.OrdinalIgnoreCase),
                HasSwitch = infrastructure.Contains(" or ", StringComparison.OrdinalIgnoreCase),
                HasSignal = infrastructure.Contains("Light", StringComparison.OrdinalIgnoreCase),
                Beacon = infrastructure.Equals("Transponder", StringComparison.OrdinalIgnoreCase) ? "pending" : "",
                InitialWaitingPassengers = station.Length > 0 ? 24 : 0,
                // Workbook has no direction/heater columns: documented simulation configuration.
                TravelDirection = "Bidirectional", HasHeater = true
            });
        }
        if (layout.Blocks.Count != 15 || !layout.Blocks.Select(b => b.Number).Order().SequenceEqual(Enumerable.Range(1, 15)))
            throw new InvalidDataException("Course Blue Line must contain each of blocks 1–15 exactly once.");
        TrackBlock Block(int n) => layout.Blocks.Single(b => b.Number == n);
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
        return layout;
    }
}
