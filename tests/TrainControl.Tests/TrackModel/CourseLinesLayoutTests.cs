using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using TrackModel.Core.Models;
using TrackModel.Core.Persistence;
using TrackModel.Core.Services;
using TrainControl.Common.Identity;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.TrackModel;

/// <summary>The three course lines as the production Track Model loads them, and the XLSX importer that produced them.</summary>
[TestClass]
public class CourseLinesLayoutTests
{
    /// <summary>The vF5 workbook's own "Infrastructure" text for the rows that carry switches and unusual station formats.</summary>
    private static readonly Dictionary<(string Line, int Number), string> WorkbookText = new()
    {
        [("Red", 7)] = "STATION: SHADYSIDE", [("Red", 9)] = "SWITCH TO/FROM YARD (75-yard)", [("Red", 15)] = "SWITCH (15-16; 1-16)",
        [("Red", 27)] = "SWITCH (27-28; 27-76); UNDERGROUND", [("Red", 32)] = "SWITCH (32-33; 33-72); UNDERGROUND",
        [("Red", 35)] = "STATION;     STEEL PLAZA; UNDERGROUND", [("Red", 38)] = "SWITCH (38-39; 38-71); UNDERGROUND",
        [("Red", 43)] = "SWITCH (43-44; 44-67); UNDERGROUND", [("Red", 52)] = "SWITCH (52-53; 52-66)",
        [("Green", 12)] = "SWITCH (12-13; 1-13)", [("Green", 16)] = "STATION", [("Green", 28)] = "SWITCH (28-29; 150-28)",
        [("Green", 39)] = "STATION; CENTRAL; UNDERDROUND", [("Green", 58)] = "SWITCH TO YARD (57-yard)", [("Green", 62)] = "SWITCH FROM YARD (Yard-63)",
        [("Green", 76)] = "SWITCH (76-77;77-101)", [("Green", 85)] = "SWITCH (85-86; 100-85)",
    };

    [TestMethod]
    public void CourseLayout_LoadsTheThreeLinesTogether_WithBlueUnchanged()
    {
        var layout = CourseTrackLayout.Create();
        CollectionAssert.AreEqual(new[] { 15, 76, 150 }, layout.Blocks.GroupBy(b => b.LineId).Select(line => line.Count()).ToArray());
        Assert.AreEqual(JsonSerializer.Serialize(BlueLineTrackLayout.Create().Blocks), JsonSerializer.Serialize(layout.Blocks.Where(b => b.LineId == "Blue").ToList()));

        var model = new TrackService();
        CourseTrackLayout.LoadDemonstration(model);
        Assert.AreEqual("001", model.FindBlock("1")!.TrainId, "The Blue demonstration train starts where it always did, with its canonical ID.");
        Assert.AreEqual(BlueLineTrackLayout.DemonstrationTrainId, model.FindBlock("1")!.TrainId);
        Assert.IsTrue(TrainIdConvention.IsCanonical(model.FindBlock("1")!.TrainId));
        Assert.IsFalse(model.Layout.Blocks.Any(b => b.Id != "1" && b.IsOccupied));
        CollectionAssert.AreEqual(new[] { "Blue Line", "Red Line", "Green Line" }, model.CreateLayoutMessage().Lines.Select(line => line.Name).ToArray());
        Assert.AreEqual("GREEN-12", model.CreateTrainEnvironment("GREEN-13").NextBlockId, "Switch 13 starts Normal, toward block 12.");
        Assert.AreEqual("Reverse", model.CreateTrainEnvironment("GREEN-5").TravelDirection);
    }

    /// <summary>
    /// Production start-up (TrackModel.Wpf's App and Restore course lines) loads all three lines
    /// with no trains, so no fleet ID is occupied before CTC dispatches that train.
    /// </summary>
    [TestMethod]
    public void ProductionLoad_HasAllThreeLinesAndNoTrains()
    {
        var model = new TrackService();
        CourseTrackLayout.Load(model);

        CollectionAssert.AreEqual(new[] { 15, 76, 150 }, model.Layout.Blocks.GroupBy(b => b.LineId).Select(line => line.Count()).ToArray());
        Assert.IsFalse(model.Layout.Blocks.Any(b => b.IsOccupied || b.TrainId.Length > 0), "No block is occupied at start-up.");
        Assert.AreEqual(new TimeSpan(9, 0, 0), model.SystemTime);
        Assert.IsFalse(model.Layout.Blocks.Any(b => b.TrainId == "001"), "Train 001 is not pre-occupied.");
    }

    [TestMethod]
    public void BlueLineProductionLoad_HasNoTrains_AndDemonstrationSeedsCanonical001()
    {
        var production = new TrackService();
        BlueLineTrackLayout.Load(production);
        Assert.IsFalse(production.Layout.Blocks.Any(b => b.IsOccupied));

        var demonstration = new TrackService();
        BlueLineTrackLayout.LoadDemonstration(demonstration);
        Assert.AreEqual("001", demonstration.FindBlock("1")!.TrainId);
        Assert.IsTrue(TrainIdConvention.IsDispatchable(demonstration.FindBlock("1")!.TrainId));
        Assert.AreEqual(1, demonstration.Layout.Blocks.Count(b => b.IsOccupied));
    }

    /// <summary>
    /// Regression: when the Track Model seeded train 001 on Blue block 1 at start-up, CTC's first
    /// dispatched train (also 001) placed on Green "moved" the seeded train from Blue to Green:
    /// Blue block 1 silently cleared and Green recorded a train entry from a Blue block. The
    /// production start-up seeds nothing, so 001's placement on Green is a clean first entry.
    /// </summary>
    [TestMethod]
    public void ProductionLoad_CtcDispatchedTrain001OnGreen_IsAFirstPlacement()
    {
        var model = new TrackService();
        CourseTrackLayout.Load(model);

        model.ApplyTrainUpdate(new TrackModelTrainUpdateMessage { TrainId = "001", CurrentBlockId = "GREEN-63" });

        var green = model.FindBlock("GREEN-63")!;
        Assert.AreEqual("001", green.TrainId);
        Assert.AreEqual(string.Empty, green.TrainEntryBlockId, "001 enters the track at GREEN-63; it did not come from another line.");
        Assert.AreEqual(1, model.Layout.Blocks.Count(b => b.TrainId == "001"));
        Assert.IsFalse(model.Layout.Blocks.Any(b => b.LineId == "Blue" && b.IsOccupied));

        // The conflict the production path avoids: with the demonstration seed, the same placement
        // is applied as a cross-line move of the seeded train.
        var seeded = new TrackService();
        CourseTrackLayout.LoadDemonstration(seeded);
        seeded.ApplyTrainUpdate(new TrackModelTrainUpdateMessage { TrainId = "001", CurrentBlockId = "GREEN-63" });
        Assert.AreEqual("1", seeded.FindBlock("GREEN-63")!.TrainEntryBlockId, "Why the demonstration seed must stay out of production.");
    }

    /// <summary>
    /// The importer, given the course workbook's format and wording, reproduces the bundled layout
    /// exactly: the bundle is the importer's output, and the conversion rules are pinned.
    /// </summary>
    [TestMethod]
    public void WorkbookImport_OfTheVf5Format_ReproducesTheBundledLayout()
    {
        var bundled = CourseTrackLayout.Create();
        WithWorkbook(SheetsFrom(bundled), path =>
        {
            var imported = new TrackFileRepository().Load(path);
            Assert.AreEqual("Course Blue, Red and Green Lines (vF5)", imported.Name);
            Assert.AreEqual(JsonSerializer.Serialize(bundled.Blocks), JsonSerializer.Serialize(imported.Blocks));
        });
    }

    /// <summary>
    /// Yard entries are explicit layout data: Blue 1 (the worksheet drawing's Yard -> 1), Red 9 (its
    /// TO/FROM yard switch row) and Green 63 ("FROM YARD (Yard-63)"). Not Green 57 ("TO YARD
    /// (57-yard)" leads into the yard), not the switch rows 58/62, not Green 1 or 14.
    /// </summary>
    [TestMethod]
    public void YardEntries_AreStatedByTheCourseData_AndPublishedToCtc()
    {
        string[] expected = ["1", "RED-9", "GREEN-63"];
        CollectionAssert.AreEqual(expected, CourseTrackLayout.Create().Blocks.Where(b => b.IsYardEntry).Select(b => b.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "1" }, BlueLineTrackLayout.Create().Blocks.Where(b => b.IsYardEntry).Select(b => b.Id).ToArray());

        var model = new TrackService();
        CourseTrackLayout.Load(model);
        var published = model.CreateLayoutMessage().Lines.SelectMany(line => line.Blocks).Where(block => block.IsYardEntry).Select(block => block.BlockId).ToArray();
        CollectionAssert.AreEqual(expected, published);
    }

    [TestMethod]
    public void WorkbookImport_RejectsYardTextThatDoesNotStateTheYardEntry_AndAcceptsACorrectedRedLeg()
    {
        var sheets = SheetsFrom(CourseTrackLayout.Create());
        sheets["Green Line"][62]["G"] = "SWITCH FROM YARD (Yard-64)";
        WithWorkbook(sheets, path => StringAssert.Contains(Assert.Throws<InvalidDataException>(() => new TrackFileRepository().Load(path)).Message, "yard entry"));

        // Red 9's leg text "75-yard" contradicts its row; a corrected "9-yard" states the same entry.
        sheets = SheetsFrom(CourseTrackLayout.Create());
        sheets["Red Line"][9]["G"] = "SWITCH TO/FROM YARD (9-yard)";
        WithWorkbook(sheets, path => CollectionAssert.AreEqual(new[] { "RED-9" },
            new TrackFileRepository().Load(path).Blocks.Where(b => b.LineId == "Red" && b.IsYardEntry).Select(b => b.Id).ToArray()));
    }

    [TestMethod]
    public void CsvLayout_WithoutAnIsYardEntryColumn_StatesNoYardEntries_AndWithOneKeepsThem()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        try
        {
            File.WriteAllText(path, "Id,LineId,Number,LengthMeters,ConnectedBlockIds\nB1,Blue,1,100,B2\nB2,Blue,2,100,B1\n");
            Assert.IsFalse(new TrackFileRepository().Load(path).Blocks.Any(b => b.IsYardEntry), "Nothing is invented, not even for block 1.");

            File.WriteAllText(path, "Id,LineId,Number,LengthMeters,ConnectedBlockIds,IsYardEntry\nB1,Blue,1,100,B2,false\nB2,Blue,2,100,B1,true\n");
            CollectionAssert.AreEqual(new[] { "B2" }, new TrackFileRepository().Load(path).Blocks.Where(b => b.IsYardEntry).Select(b => b.Id).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void WorkbookImport_RejectsAWorkbookThatDoesNotMatchTheVf5Topology()
    {
        var sheets = SheetsFrom(CourseTrackLayout.Create());
        sheets["Green Line"][85]["G"] = "SWITCH (85-86; 99-85)";
        WithWorkbook(sheets, path => Assert.Throws<InvalidDataException>(() => new TrackFileRepository().Load(path)));

        sheets = SheetsFrom(CourseTrackLayout.Create());
        sheets["Red Line"].RemoveAt(40);
        WithWorkbook(sheets, path => Assert.Throws<InvalidDataException>(() => new TrackFileRepository().Load(path)));
    }

    [TestMethod]
    public void ApplyCommands_AppliesEachCommandOnItsOwn_AndNotifiesOnceOnlyWhenSomethingChanged()
    {
        var model = new TrackService();
        CourseTrackLayout.LoadDemonstration(model);
        model.ApplyTrainUpdate(new TrackModelTrainUpdateMessage { TrainId = "002", CurrentBlockId = "RED-16" });
        int notifications = 0;
        model.StateChanged += (_, _) => notifications++;

        var refused = model.ApplyCommands(
        [
            new TrackModelCommandMessage { BlockId = "RED-16", Switch = SwitchPosition.Reverse, Signal = SignalState.Red, Crossing = CrossingState.Open },
            new TrackModelCommandMessage { BlockId = "RED-11", Switch = SwitchPosition.Normal, Signal = SignalState.Red, Crossing = CrossingState.Closed },
            new TrackModelCommandMessage { BlockId = "GREEN-999", Switch = SwitchPosition.Normal, Signal = SignalState.Red, Crossing = CrossingState.Open },
            new TrackModelCommandMessage { BlockId = "GREEN-28", Switch = SwitchPosition.Reverse, Signal = SignalState.Red, Crossing = CrossingState.Open, AuthorityMeters = 50, CommandedSpeedMetersPerSecond = 5 },
        ]);

        Assert.HasCount(2, refused);
        StringAssert.StartsWith(refused[0], "RED-16: An occupied switch cannot be thrown");
        StringAssert.StartsWith(refused[1], "GREEN-999:");
        Assert.AreEqual(SwitchPosition.Normal, model.FindBlock("RED-16")!.Switch, "The refused command changed nothing.");
        Assert.AreEqual(CrossingState.Closed, model.FindBlock("RED-11")!.Crossing, "Later commands still applied.");
        Assert.AreEqual(SwitchPosition.Reverse, model.FindBlock("GREEN-28")!.Switch);
        Assert.AreEqual(50, model.FindBlock("GREEN-28")!.AuthorityMeters);
        Assert.AreEqual(1, notifications);

        Assert.IsEmpty(model.ApplyCommands([new TrackModelCommandMessage { BlockId = "RED-11", Switch = SwitchPosition.Normal, Signal = SignalState.Red, Crossing = CrossingState.Closed }]));
        Assert.AreEqual(1, notifications, "An unchanged repeat is not a state change.");
    }

    /// <summary>Course-format worksheets (row 1 headers, then one row per block) rebuilt from a layout, with the workbook's wording.</summary>
    private static Dictionary<string, List<Dictionary<string, string>>> SheetsFrom(TrackLayout layout)
    {
        var sheets = new Dictionary<string, List<Dictionary<string, string>>>();
        foreach (var line in layout.Blocks.GroupBy(b => b.LineId))
        {
            var rows = new List<Dictionary<string, string>>
            {
                new() { ["A"] = "Line", ["B"] = "Section", ["C"] = "Block Number", ["D"] = "Block Length (m)", ["E"] = "Block Grade (%)", ["F"] = "Speed Limit (Km/Hr)", ["G"] = "Infrastructure", ["H"] = "Station Side" },
            };
            foreach (var b in line.OrderBy(b => b.Number))
            {
                string text = WorkbookText.TryGetValue((line.Key, b.Number), out var literal) ? literal
                    : line.Key == "Blue" ? BlueText(b)
                    : b.StationName.Length > 0 ? "STATION; " + b.StationName.ToUpperInvariant()
                    : b.HasCrossing ? "RAILWAY CROSSING" : "";
                rows.Add(new()
                {
                    ["A"] = line.Key, ["B"] = b.Section, ["C"] = b.Number.ToString(CultureInfo.InvariantCulture),
                    ["D"] = b.LengthMeters.ToString("R", CultureInfo.InvariantCulture), ["E"] = b.GradePercent.ToString("R", CultureInfo.InvariantCulture),
                    ["F"] = (b.SpeedLimitMetersPerSecond * 3.6).ToString("0.######", CultureInfo.InvariantCulture), ["G"] = text,
                });
            }

            sheets[line.Key + " Line"] = rows;
        }

        return sheets;
    }

    private static string BlueText(TrackBlock b) => b.Number switch
    {
        3 => "RAILWAY CROSSING", 5 => "Switch ( 5 to 6) or (5 to 11)", 6 => "Switch ( 5 to 6); Light", 11 => "Switch (5 to 11); Light",
        9 or 14 => "Transponder", 10 => "Station B", 15 => "Station C", _ => "",
    };

    private static void WithWorkbook(Dictionary<string, List<Dictionary<string, string>>> sheets, Action<string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                void Entry(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
                const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var names = sheets.Keys.ToList();
                Entry("xl/workbook.xml", $"<workbook xmlns='{ns}' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets>"
                    + string.Concat(names.Select((name, i) => $"<sheet name='{name}' sheetId='{i + 1}' r:id='s{i}'/>")) + "</sheets></workbook>");
                Entry("xl/_rels/workbook.xml.rels", "<Relationships>" + string.Concat(names.Select((_, i) => $"<Relationship Id='s{i}' Target='worksheets/sheet{i + 1}.xml'/>")) + "</Relationships>");
                for (int i = 0; i < names.Count; i++)
                {
                    var sheet = new StringBuilder($"<worksheet xmlns='{ns}'><sheetData>");
                    var rows = sheets[names[i]];
                    for (int r = 0; r < rows.Count; r++)
                    {
                        sheet.Append($"<row r='{r + 1}'>");
                        foreach (var (column, value) in rows[r].Where(cell => cell.Value.Length > 0))
                        {
                            bool number = r > 0 && column is "C" or "D" or "E" or "F";
                            sheet.Append(number ? $"<c r='{column}{r + 1}'><v>{value}</v></c>" : $"<c r='{column}{r + 1}' t='inlineStr'><is><t>{SecurityElement.Escape(value)}</t></is></c>");
                        }

                        sheet.Append("</row>");
                    }

                    Entry($"xl/worksheets/sheet{i + 1}.xml", sheet.Append("</sheetData></worksheet>").ToString());
                }
            }

            test(path);
        }
        finally { File.Delete(path); }
    }
}
