using System.IO.Compression;
using System.Text;
using TrackModel.Core.Persistence;
using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.TrackModel;

[TestClass]
public class BlueLineLayoutTests
{
    [TestMethod]
    public void CourseLayout_MatchesAllFifteenWorkbookBlocksAndEquipment()
    {
        var layout = BlueLineTrackLayout.Create();
        Assert.HasCount(15, layout.Blocks);
        CollectionAssert.AreEqual(Enumerable.Range(1,15).ToArray(),layout.Blocks.Select(b=>b.Number).ToArray());
        foreach (var b in layout.Blocks)
        {
            Assert.AreEqual("Blue",b.LineId); Assert.AreEqual(50d,b.LengthMeters);
            Assert.AreEqual(0d,b.GradePercent); Assert.AreEqual(0d,b.ElevationMeters);
            Assert.AreEqual(50/3.6,b.SpeedLimitMetersPerSecond,1e-9);
            Assert.AreEqual(b.Number<=5?"A":b.Number<=10?"B":"C",b.Section);
        }
        CollectionAssert.AreEqual(new[]{5},layout.Blocks.Where(b=>b.HasSwitch).Select(b=>b.Number).ToArray());
        CollectionAssert.AreEqual(new[]{6,11},layout.Blocks.Where(b=>b.HasSignal).Select(b=>b.Number).ToArray());
        CollectionAssert.AreEqual(new[]{3},layout.Blocks.Where(b=>b.HasCrossing).Select(b=>b.Number).ToArray());
        CollectionAssert.AreEqual(new[]{9,14},layout.Blocks.Where(b=>b.HasBeacon).Select(b=>b.Number).ToArray());
        CollectionAssert.AreEqual(new[]{10,15},layout.Blocks.Where(b=>b.StationName.Length>0).Select(b=>b.Number).ToArray());
        Assert.AreEqual(28,layout.Blocks.Sum(b=>b.ConnectedBlockIds.Count));
        foreach(var b in layout.Blocks)
            foreach(var next in b.ConnectedBlockIds) CollectionAssert.Contains(layout.Blocks.Single(x=>x.Id==next).ConnectedBlockIds,b.Id);
        Assert.HasCount(1,layout.Blocks.Single(b=>b.Id=="10").ConnectedBlockIds);
        Assert.HasCount(1,layout.Blocks.Single(b=>b.Id=="15").ConnectedBlockIds);
    }

    [TestMethod]
    public void Beacon_IsOnApproachTransponderAndLayoutExportsPhysicalProperties()
    {
        var model=new TrackService(); model.LoadLayout(BlueLineTrackLayout.Create());
        Assert.AreEqual("Station B",model.CreateTrainEnvironment("9").Beacon);
        Assert.AreEqual("10",model.CreateTrainEnvironment("9").BeaconTargetBlockId);
        Assert.AreEqual("Station C",model.CreateTrainEnvironment("14").Beacon);
        Assert.AreEqual("",model.CreateTrainEnvironment("10").Beacon);
        var definitions=model.CreateLayoutMessage().Lines.Single().Blocks;
        Assert.AreEqual(50/3.6,definitions[0].SpeedLimitMetersPerSecond,1e-9);
        Assert.AreEqual("Bidirectional",definitions[0].TravelDirection);
        Assert.IsTrue(definitions[0].HasHeater);
        Assert.AreEqual("6",definitions.Single(b=>b.BlockId=="5").NormalNextBlockId);
        Assert.AreEqual("11",definitions.Single(b=>b.BlockId=="5").ReverseNextBlockId);
    }

    [TestMethod]
    public void Demand_ChangesWaitingOnlyAndRejectsInvalidPopulationAtomically()
    {
        var model=new TrackService(); model.LoadLayout(BlueLineTrackLayout.Create());
        model.ApplyPassengerDemand(new(){BlockId="10",WaitingPassengers=7});
        Assert.AreEqual(7,model.CreateTrainEnvironment("10").WaitingPassengers);
        Assert.AreEqual(0,model.CreateTicketSales("Blue").TicketsPerHour);
        Assert.Throws<ArgumentException>(()=>model.ApplyPassengerDemand(new(){BlockId="10",WaitingPassengers=-1}));
        Assert.Throws<ArgumentException>(()=>model.ApplyPassengerDemand(new(){BlockId="9",WaitingPassengers=7}));
        Assert.AreEqual(7,model.CreateTrainEnvironment("10").WaitingPassengers);
    }

    [TestMethod]
    public void BlueSwitch_IsSafeWhileOccupiedAndSelectsBothRealBranches()
    {
        var model=new TrackService(); model.LoadLayout(BlueLineTrackLayout.Create());
        model.ApplyCommand(new(){BlockId="5",Switch=SwitchPosition.Reverse});
        Assert.AreEqual("11",model.CreateTrainEnvironment("5").NextBlockId);
        model.ApplyTrainUpdate(new(){TrainId="01",CurrentBlockId="5"});
        Assert.Throws<InvalidOperationException>(()=>model.ApplyCommand(new(){BlockId="5",Switch=SwitchPosition.Normal}));
        Assert.AreEqual("11",model.CreateTrainEnvironment("5").NextBlockId);
        model.ApplyTrainUpdate(new(){TrainId="01",CurrentBlockId="4"});
        model.ApplyCommand(new(){BlockId="5",Switch=SwitchPosition.Normal});
        Assert.AreEqual("6",model.CreateTrainEnvironment("5").NextBlockId);
    }

    [TestMethod]
    public void WorkbookImport_ReadsInlineAndSharedStringsWithoutCachedElevationFormulas()
    {
        WithWorkbook(path=>
        {
            var imported=new TrackFileRepository().Load(path);
            Assert.HasCount(15,imported.Blocks);
            Assert.AreEqual(50d,imported.Blocks[0].LengthMeters);
            Assert.AreEqual(0d,imported.Blocks[14].ElevationMeters);
            Assert.AreEqual("Station B",imported.Blocks[8].Beacon);
            Assert.AreEqual("11",imported.Blocks[4].ReverseNextBlockId);
        });
    }

    [TestMethod]
    public void WorkbookImport_RejectsWrongHeaderAndMissingBlock()
    {
        WithWorkbook(path=>Assert.Throws<InvalidDataException>(()=>new TrackFileRepository().Load(path)),badHeader:true);
        WithWorkbook(path=>Assert.Throws<InvalidDataException>(()=>new TrackFileRepository().Load(path)),missingBlock:true);
    }

    private static void WithWorkbook(Action<string> test,bool badHeader=false,bool missingBlock=false)
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".xlsx");
        try
        {
            using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))
            {
                void Entry(string name,string text){using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write(text);}
                const string ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                Entry("xl/workbook.xml",$"<workbook xmlns='{ns}' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Blue Line' sheetId='1' r:id='blue'/></sheets></workbook>");
                Entry("xl/_rels/workbook.xml.rels","<Relationships><Relationship Id='blue' Target='/xl/worksheets/sheet1.xml'/></Relationships>");
                Entry("xl/sharedStrings.xml",$"<sst xmlns='{ns}'><si><t>Block Number</t></si></sst>");
                string Inline(string cell,string value)=>$"<c r='{cell}' t='inlineStr'><is><t>{value}</t></is></c>";
                var sheet=new StringBuilder($"<worksheet xmlns='{ns}'><sheetData><row r='1'>");
                sheet.Append("<c r='C1' t='s'><v>0</v></c>");
                sheet.Append(Inline("D1",badHeader?"Length (ft)":"Block Length (m)"));
                sheet.Append(Inline("E1","Block Grade (%)")); sheet.Append(Inline("F1","Speed Limit (Km/Hr)"));
                sheet.Append(Inline("G1","Infrastructure"));sheet.Append("</row>");
                for(var n=1;n<=(missingBlock?14:15);n++)
                {
                    var r=n+1; var infrastructure=n switch{3=>"RAILWAY CROSSING",5=>"Switch ( 5 to 6) or (5 to 11)",6 or 11=>"Switch; Light",9 or 14=>"Transponder",10=>"Station B",15=>"Station C",_=>""};
                    sheet.Append($"<row r='{r}'>"+Inline("B"+r,n<=5?"A":n<=10?"B":"C")+$"<c r='C{r}'><v>{n}</v></c><c r='D{r}'><v>50</v></c><c r='E{r}'><v>0</v></c><c r='F{r}'><v>50</v></c>"+Inline("G"+r,infrastructure)+$"<c r='I{r}'><f>E{r}*D{r}/100</f></c></row>");
                }
                sheet.Append("</sheetData></worksheet>");Entry("xl/worksheets/sheet1.xml",sheet.ToString());
            }
            test(path);
        }
        finally{File.Delete(path);}
    }
}
