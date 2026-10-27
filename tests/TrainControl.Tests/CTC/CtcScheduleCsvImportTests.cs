using CTC.Core.Models;
using CTC.Core.Scheduling;
using CTC.Core.Services;

namespace TrainControl.Tests.CTC;

[TestClass]
public class CtcScheduleCsvImportTests
{
    /// <summary>Same contents as src/CTC/CTC.Wpf/SampleData/BlueLineSampleSchedule.csv.</summary>
    private const string SampleCsv =
        "Line,Section,Block Number,Train 1,Train 2,Train 3,Train 4\r\n"
        + "Blue,A,1,12:00:00,12:03:00,12:06:00,12:09:00\r\n"
        + "Blue,B,10,12:00:36,,12:06:36,\r\n"
        + "Blue,C,15,,12:03:36,,12:09:36\r\n";

    private const string Header = "Line,Section,Block Number,Train 1,Train 2\n";

    private static CtcLineState BlueLineState(CTCService? service = null) =>
        (service ?? BlueLine.CreateService()).State.FindLine(BlueLine.LineId)!;

    private static ScheduleCsvImportResult Import(string csv, int firstTrainNumber = 0) =>
        ScheduleCsvImporter.Import(BlueLineState(), csv, firstTrainNumber);

    private static ScheduleTemplateRow Row(ScheduleTemplate template, string blockId) =>
        template.Rows.Single(row => row.BlockId == blockId);

    private static string[] RouteOf(ScheduledTrain train) => train.Route.Select(block => block.BlockId).ToArray();

    [TestMethod]
    public void Import_SparseSample_FillsOnlyListedBlocks()
    {
        var result = Import(SampleCsv);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        var template = result.Template!;
        Assert.HasCount(15, template.Rows);
        Assert.HasCount(4, template.TrainIds);
        CollectionAssert.AreEqual(new[] { "12:00:00", "12:03:00", "12:06:00", "12:09:00" }, Row(template, "A1").TrainTimes);
        CollectionAssert.AreEqual(new[] { "12:00:36", "", "12:06:36", "" }, Row(template, "B10").TrainTimes);
        CollectionAssert.AreEqual(new[] { "", "12:03:36", "", "12:09:36" }, Row(template, "C15").TrainTimes);
        Assert.IsTrue(template.Rows
            .Where(row => row.BlockId is not ("A1" or "B10" or "C15"))
            .All(row => row.TrainTimes.All(time => time == string.Empty)));
    }

    [TestMethod]
    public void Import_KeepsTrackDataFromLayout()
    {
        var template = Import(SampleCsv).Template!;

        Assert.AreEqual(BlueLine.LineId, template.LineId);
        Assert.IsTrue(Row(template, "A1").IsRouteStart);
        Assert.AreEqual(BlueLine.BlockLengthMeters, Row(template, "B10").LengthMeters);
        CollectionAssert.AreEqual(new[] { "A4", "B6", "C11" }, Row(template, "A5").ConnectedBlockIds.ToArray());
    }

    [TestMethod]
    public void Import_TrainIdsComeFromFactoryNotHeaders()
    {
        var template = Import(SampleCsv).Template!;

        CollectionAssert.AreEqual(new[] { "000", "001", "002", "003" }, template.TrainIds.ToArray());
    }

    [TestMethod]
    public void Import_EarlierTrainIdsExist_ContinuesNumbering()
    {
        var service = BlueLine.CreateService();
        service.QueueSchedule(
        [
            BlueLine.Train("000", new TimeSpan(11, 0, 0)),
            BlueLine.Train("001", new TimeSpan(11, 0, 10)),
        ]);

        var result = ScheduleCsvImporter.Import(BlueLineState(service), SampleCsv, TrainIds.NextAvailableNumber(service.State));

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        CollectionAssert.AreEqual(new[] { "002", "003", "004", "005" }, result.Template!.TrainIds.ToArray());
    }

    [TestMethod]
    public void Import_SampleConvertsToBranchRoutes()
    {
        var conversion = ScheduleTemplateConverter.Convert(Import(SampleCsv).Template!);

        Assert.IsTrue(conversion.IsSuccess, conversion.ErrorMessage);
        Assert.HasCount(4, conversion.Trains);
        CollectionAssert.AreEqual(BlueLine.BranchB, RouteOf(conversion.Trains[0]));
        CollectionAssert.AreEqual(BlueLine.BranchC, RouteOf(conversion.Trains[1]));
        CollectionAssert.AreEqual(BlueLine.BranchB, RouteOf(conversion.Trains[2]));
        CollectionAssert.AreEqual(BlueLine.BranchC, RouteOf(conversion.Trains[3]));
    }

    [TestMethod]
    public void Import_DoesNotChangeCtcState()
    {
        var service = BlueLine.CreateService();

        ScheduleCsvImporter.Import(BlueLineState(service), SampleCsv);

        Assert.IsEmpty(service.State.ScheduledTrains);
        Assert.IsEmpty(service.State.DispatchQueue);
    }

    [TestMethod]
    [DataRow("Blue")]
    [DataRow("BLUE")]
    [DataRow("Blue Line")]
    [DataRow(" blue line ")]
    public void Import_LineNameVariants_Accepted(string lineText)
    {
        var result = Import(Header + $"{lineText},A,1,12:00:00,12:01:00\n");

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
    }

    [TestMethod]
    public void Import_WrongLine_Fails()
    {
        var result = Import(Header + "Red,A,1,12:00:00,12:01:00\n");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.ErrorMessage, "the schedule is for Red Line, but Blue Line is selected");
    }

    [TestMethod]
    public void Import_UnknownBlock_Fails()
    {
        var result = Import(Header + "Blue,Z,999,12:00:00,12:01:00\n");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Block Z999 does not exist on Blue Line.", result.ErrorMessage);
    }

    [TestMethod]
    public void Import_SectionMatchesIgnoringCase()
    {
        var result = Import(Header + "Blue,a,1,12:00:00,12:01:00\nBlue,b,10,12:00:36,\n");

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual("12:00:36", Row(result.Template!, "B10").TrainTimes[0]);
    }

    [TestMethod]
    public void Import_DuplicateBlock_Fails()
    {
        var result = Import(Header + "Blue,A,1,12:00:00,12:01:00\nBlue,B,10,12:00:36,\nBlue,B,10,,12:01:36\n");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Block B10 appears more than once.", result.ErrorMessage);
    }

    [TestMethod]
    public void Import_InvalidBlockNumber_Fails()
    {
        var result = Import(Header + "Blue,A,ABC,12:00:00,12:01:00\n");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.ErrorMessage, "'ABC' is not a valid block number.");
    }

    [TestMethod]
    public void Import_NoTrainColumns_Fails()
    {
        var result = Import("Line,Section,Block Number\nBlue,A,1\n");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("The schedule contains no train columns.", result.ErrorMessage);
    }

    [TestMethod]
    [DataRow("Section,Block Number,Train 1\nA,1,12:00:00\n")]
    [DataRow("Line,Block Number,Section,Train 1\nBlue,1,A,12:00:00\n")]
    [DataRow("Blue,A,1,12:00:00\n")]
    public void Import_MissingRequiredHeader_Fails(string csv)
    {
        var result = Import(csv);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.ErrorMessage, "Required columns Line, Section, and Block Number were not found.");
    }

    [TestMethod]
    public void Import_EmptyFile_Fails()
    {
        Assert.IsFalse(Import("").IsSuccess);
        Assert.IsFalse(Import("\r\n\r\n").IsSuccess);
    }

    [TestMethod]
    public void Import_BlankLines_AreIgnored()
    {
        var csv = "\r\n" + SampleCsv.Replace("\r\nBlue,B", "\r\n\r\n   \r\n,,,,,,\r\nBlue,B") + "\r\n\r\n";

        var result = Import(csv);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        CollectionAssert.AreEqual(new[] { "12:00:36", "", "12:06:36", "" }, Row(result.Template!, "B10").TrainTimes);
    }

    [TestMethod]
    public void Import_WrongColumnCount_Fails()
    {
        var result = Import(Header + "Blue,A,1,12:00:00\n");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Row 2 has 4 column(s), but the header has 5.", result.ErrorMessage);
    }

    [TestMethod]
    public void Import_QuotedFields_AreParsed()
    {
        var csv = "\"Line\",\"Section\",\"Block Number\",\"Train 1, express\",\"Train \"\"2\"\"\"\n"
            + "\"Blue Line\",\"A\",\"1\",\"12:00:00\",\"12:01:00\"\n";

        var result = Import(csv);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.HasCount(2, result.Template!.TrainIds);
        CollectionAssert.AreEqual(new[] { "12:00:00", "12:01:00" }, Row(result.Template, "A1").TrainTimes);
    }

    [TestMethod]
    public void Import_UnclosedQuote_Fails()
    {
        var result = Import(Header + "Blue,A,1,\"12:00:00,12:01:00\n");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.ErrorMessage, "no closing quote");
    }

    [TestMethod]
    public void Import_ByteOrderMarkAndWhitespace_AreIgnored()
    {
        var result = Import("﻿ Line , Section , Block Number , Train 1\n Blue , A , 1 , 12:00:00 \n");

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual("12:00:00", Row(result.Template!, "A1").TrainTimes[0]);
    }

    [TestMethod]
    public void Import_InvalidTimeText_IsLeftForConverter()
    {
        // The importer checks structure only; the converter reports bad times when the schedule is queued.
        var result = Import(Header + "Blue,A,1,12:8,12:01:00\nBlue,B,10,12:00:36,12:01:36\n");

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual("12:8", Row(result.Template!, "A1").TrainTimes[0]);

        var conversion = ScheduleTemplateConverter.Convert(result.Template!);
        Assert.IsFalse(conversion.IsSuccess);
        StringAssert.Contains(conversion.ErrorMessage, "invalid route start time '12:8'");
    }

    [TestMethod]
    public void Import_TooManyTrainsForRemainingIds_Fails()
    {
        var result = Import(SampleCsv, firstTrainNumber: TrainIds.MaxNumber - 1);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.ErrorMessage, "more train ID(s) are available");
    }
}
