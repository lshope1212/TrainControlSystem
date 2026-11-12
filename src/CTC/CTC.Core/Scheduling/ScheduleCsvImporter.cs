using System.Globalization;
using System.Text;
using CTC.Core.Models;

namespace CTC.Core.Scheduling;

/// <summary>Outcome of <see cref="ScheduleCsvImporter.Import"/>.</summary>
public sealed class ScheduleCsvImportResult
{
    private ScheduleCsvImportResult(ScheduleTemplate? template, string? errorMessage, IReadOnlyList<string>? trainsNeedingStart = null)
    {
        Template = template;
        ErrorMessage = errorMessage;
        TrainsNeedingStartSelection = trainsNeedingStart ?? Array.Empty<string>();
    }

    public bool IsSuccess => ErrorMessage is null;

    /// <summary>Dispatcher-readable reason the file was rejected; null on success.</summary>
    public string? ErrorMessage { get; }

    /// <summary>The filled template on success; null on failure.</summary>
    public ScheduleTemplate? Template { get; }

    /// <summary>
    /// Train IDs whose starting block the file does not determine unambiguously; the dispatcher must
    /// select it before queueing. Empty when every train's start is known.
    /// </summary>
    public IReadOnlyList<string> TrainsNeedingStartSelection { get; }

    internal static ScheduleCsvImportResult Success(ScheduleTemplate template, IReadOnlyList<string> trainsNeedingStart) => new(template, null, trainsNeedingStart);

    internal static ScheduleCsvImportResult Failure(string errorMessage) => new(null, errorMessage);
}

/// <summary>
/// Reads a schedule CSV into a <see cref="ScheduleTemplate"/>, the same input format the
/// manual Schedule Builder fills in.
/// </summary>
/// <remarks>
/// Format: a header <c>Line,Section,Block Number,&lt;train&gt;,...</c>, then one row per block
/// the dispatcher wants to time. Every column after Block Number is one train; the header
/// text of those columns is only a label, and the real train IDs come from
/// <see cref="ScheduleTemplateFactory"/>. The file is sparse: the template is generated from
/// the line's track layout and only the listed blocks get times, so
/// <see cref="ScheduleTemplateConverter"/> routes trains through the blank blocks exactly as
/// it does for a manually entered schedule. This importer only checks the file's structure;
/// time text is copied as-is and validated by the converter when the schedule is queued.
/// <para>
/// An optional <c>DwellTimeSeconds</c> column (anywhere after Block Number) gives the dwell, in
/// whole seconds, for every train timed at that row's block. When the column or a value is
/// missing, each stop gets its block's default from <see cref="DwellTimePolicy"/> (45 s at a
/// station, otherwise 0 s), so files written before dwell times existed import unchanged.
/// </para>
/// <para>
/// Starting blocks. The file has no starting-block column; each train's start is taken from the
/// line's yard entries (<see cref="YardEntries"/>), never from whichever block happens to be timed
/// first. A train timed at exactly one yard entry starts there (that time is its departure); on a
/// line with a single yard entry a train starts there regardless. Otherwise (several entries timed,
/// or none timed on a line with several) the start is left unselected and the dispatcher must choose
/// it in the Schedule Builder (<see cref="ScheduleCsvImportResult.TrainsNeedingStartSelection"/>)
/// before the schedule can be queued. Files written for a single-entry line therefore import as before.
/// </para>
/// </remarks>
public static class ScheduleCsvImporter
{
    private static readonly string[] RequiredColumns = ["Line", "Section", "Block Number"];

    /// <summary>Accepted headers for the optional dwell column (matched ignoring case and spaces).</summary>
    private static readonly string[] DwellColumnNames = ["DwellTimeSeconds", "Dwell Time (sec)", "Dwell (sec)"];

    /// <param name="line">The line the schedule is for; the CSV must name this line.</param>
    /// <param name="csv">The file contents.</param>
    /// <param name="firstTrainNumber">
    /// First train number to use ("001" by default); the train columns get consecutive IDs up to
    /// "010". The Schedule Builder passes <see cref="TrainIds.AvailableIds"/> instead.
    /// </param>
    public static ScheduleCsvImportResult Import(CtcLineState line, string csv, int firstTrainNumber = TrainIds.FirstNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstTrainNumber, TrainIds.FirstNumber);

        return Import(line, csv, ScheduleTemplateFactory.SupportedIdsFrom(firstTrainNumber));
    }

    /// <param name="line">The line the schedule is for; the CSV must name this line.</param>
    /// <param name="csv">The file contents.</param>
    /// <param name="availableTrainIds">
    /// The train IDs the schedule may use, lowest first (see <see cref="TrainIds.AvailableIds"/>);
    /// the train columns take the first ones. A file with more train columns than available IDs
    /// is rejected with the same message the Schedule Builder shows.
    /// </param>
    public static ScheduleCsvImportResult Import(CtcLineState line, string csv, IReadOnlyList<string> availableTrainIds)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(availableTrainIds);

        string? parseError = TryParseCsv(csv, out var records);
        if (parseError is not null)
        {
            return ScheduleCsvImportResult.Failure(parseError);
        }

        // Spreadsheet programs often save empty rows as a line of commas, so treat those as blank too.
        records = records.Where(record => record.Fields.Any(field => !string.IsNullOrWhiteSpace(field))).ToList();
        if (records.Count == 0)
        {
            return ScheduleCsvImportResult.Failure("The file is empty.");
        }

        var header = records[0].Fields.Select(field => field.Trim()).ToList();
        bool hasRequiredColumns = header.Count >= RequiredColumns.Length
            && RequiredColumns.Select((name, i) => IsColumnName(header[i], name)).All(matches => matches);
        if (!hasRequiredColumns)
        {
            return ScheduleCsvImportResult.Failure(
                "Required columns Line, Section, and Block Number were not found. The first row must start with Line,Section,Block Number.");
        }

        // An optional dwell column may sit anywhere after Block Number; every other column is a train.
        var dwellColumns = Enumerable.Range(RequiredColumns.Length, header.Count - RequiredColumns.Length)
            .Where(index => DwellColumnNames.Any(name => IsColumnName(header[index], name)))
            .ToList();
        if (dwellColumns.Count > 1)
        {
            return ScheduleCsvImportResult.Failure($"The header has more than one {DwellColumnNames[0]} column.");
        }

        int? dwellColumn = dwellColumns.Count == 1 ? dwellColumns[0] : null;
        var trainFieldIndexes = Enumerable.Range(RequiredColumns.Length, header.Count - RequiredColumns.Length)
            .Where(index => index != dwellColumn)
            .ToList();
        int trainCount = trainFieldIndexes.Count;
        if (trainCount == 0)
        {
            return ScheduleCsvImportResult.Failure("The schedule contains no train columns.");
        }

        ScheduleTemplate template;
        try
        {
            template = ScheduleTemplateFactory.Create(line, TrainIds.Take(availableTrainIds, trainCount));
        }
        catch (InvalidOperationException ex)
        {
            return ScheduleCsvImportResult.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            // An unsupported or repeated ID in availableTrainIds; the message is dispatcher-readable.
            return ScheduleCsvImportResult.Failure(ex.Message);
        }

        // The same restriction as a generated template: without yard entries no train can start anywhere.
        if (template.YardEntryBlockIds.Count == 0)
        {
            return ScheduleCsvImportResult.Failure(YardEntries.NoneConfiguredMessage(line));
        }

        string lineName = DisplayLineName(line);
        var timedBlockIds = new HashSet<string>();
        var parsed = new List<(CsvRecord Record, ScheduleTemplateRow Row)>();
        foreach (var record in records.Skip(1))
        {
            var fields = record.Fields;
            if (fields.Count != header.Count)
            {
                return ScheduleCsvImportResult.Failure(
                    $"Row {record.LineNumber} has {fields.Count} column(s), but the header has {header.Count}.");
            }

            string lineText = fields[0].Trim();
            if (!IsSameLine(lineText, line))
            {
                string scheduleLine = lineText.Length == 0 ? "no line" : WithLineSuffix(lineText);
                return ScheduleCsvImportResult.Failure($"Row {record.LineNumber}: the schedule is for {scheduleLine}, but {lineName} is selected.");
            }

            string section = fields[1].Trim();
            string numberText = fields[2].Trim();
            if (!int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int blockNumber))
            {
                return ScheduleCsvImportResult.Failure($"Row {record.LineNumber}: '{numberText}' is not a valid block number.");
            }

            var row = template.Rows.FirstOrDefault(row =>
                row.BlockNumber == blockNumber && string.Equals(row.Section, section, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                return ScheduleCsvImportResult.Failure($"Block {section}{blockNumber} does not exist on {lineName}.");
            }

            if (!timedBlockIds.Add(row.BlockId))
            {
                return ScheduleCsvImportResult.Failure($"Block {row.BlockId} appears more than once.");
            }

            parsed.Add((record, row));
        }

        // Starts first, so a start block's time is read as the departure (no dwell) from the outset.
        var needingStart = new List<string>();
        for (int column = 0; column < trainCount; column++)
        {
            int field = trainFieldIndexes[column];
            var timedEntries = parsed
                .Where(item => item.Row.IsYardEntry && !string.IsNullOrWhiteSpace(item.Record.Fields[field]))
                .Select(item => item.Row.BlockId)
                .ToList();
            if (timedEntries.Count == 1)
            {
                template.InitializeStartBlock(column, timedEntries[0]);
            }
            else if (template.YardEntryBlockIds.Count != 1)
            {
                // Several timed entries, or none on a line with several: ambiguous, so the dispatcher chooses.
                needingStart.Add(template.TrainIds[column]);
            }
        }

        foreach (var (record, row) in parsed)
        {
            var fields = record.Fields;
            string dwellText = dwellColumn is { } index ? fields[index].Trim() : string.Empty;
            for (int column = 0; column < trainCount; column++)
            {
                row.SetTrainTime(column, fields[trainFieldIndexes[column]].Trim());

                // The row's dwell applies to every train timed at this block. Blank (or no dwell
                // column at all) keeps the block's default: 45 s at a station, otherwise 0 s.
                if (dwellText.Length > 0 && !string.IsNullOrWhiteSpace(row.TrainTimes[column]))
                {
                    string? dwellError = row.TrySetTrainDwellTime(column, dwellText);
                    if (dwellError is not null)
                    {
                        return ScheduleCsvImportResult.Failure($"Row {record.LineNumber}: {dwellError}");
                    }
                }
            }
        }

        return ScheduleCsvImportResult.Success(template, needingStart);
    }

    /// <summary>Header match ignoring case and spaces, so "Block Number" and "BlockNumber" both work.</summary>
    private static bool IsColumnName(string text, string name) =>
        string.Equals(text.Replace(" ", string.Empty), name.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);

    /// <summary>"Blue", "BLUE" and "Blue Line" all name the line with ID BLUE / name "Blue Line".</summary>
    private static bool IsSameLine(string text, CtcLineState line)
    {
        string wanted = WithoutLineSuffix(text);
        return wanted.Length > 0
            && (string.Equals(wanted, WithoutLineSuffix(line.LineId), StringComparison.OrdinalIgnoreCase)
                || string.Equals(wanted, WithoutLineSuffix(line.Name), StringComparison.OrdinalIgnoreCase));
    }

    private static string WithoutLineSuffix(string text)
    {
        text = text.Trim();
        return text.EndsWith(" line", StringComparison.OrdinalIgnoreCase) ? text[..^" line".Length].TrimEnd() : text;
    }

    private static string WithLineSuffix(string text) =>
        text.EndsWith(" line", StringComparison.OrdinalIgnoreCase) ? text : $"{text} Line";

    private static string DisplayLineName(CtcLineState line) =>
        string.IsNullOrWhiteSpace(line.Name) ? WithLineSuffix(line.LineId) : line.Name;

    private sealed record CsvRecord(int LineNumber, List<string> Fields);

    /// <summary>
    /// Splits CSV text into records (RFC 4180): fields are separated by commas, a field in
    /// double quotes may contain commas, line breaks and doubled quotes ("") for a quote.
    /// Returns an error message, or null with <paramref name="records"/> set.
    /// </summary>
    private static string? TryParseCsv(string csv, out List<CsvRecord> records)
    {
        records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        int lineNumber = 1;
        int recordStartLine = 1;

        // Skip a UTF-8 byte order mark, which spreadsheet programs often write.
        int i = csv.Length > 0 && csv[0] == '﻿' ? 1 : 0;
        for (; i < csv.Length; i++)
        {
            char c = csv[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        lineNumber++;
                    }

                    field.Append(c);
                }
            }
            else if (c == '"' && field.ToString().Trim().Length == 0)
            {
                // Opening quote; whitespace before it is not part of the value.
                field.Clear();
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                {
                    i++;
                }

                fields.Add(field.ToString());
                field.Clear();
                records.Add(new CsvRecord(recordStartLine, fields));
                fields = new List<string>();
                lineNumber++;
                recordStartLine = lineNumber;
            }
            else
            {
                field.Append(c);
            }
        }

        if (inQuotes)
        {
            return $"Row {recordStartLine} has a quoted value with no closing quote.";
        }

        // Last line without a trailing line break.
        if (fields.Count > 0 || field.Length > 0)
        {
            fields.Add(field.ToString());
            records.Add(new CsvRecord(recordStartLine, fields));
        }

        return null;
    }
}
