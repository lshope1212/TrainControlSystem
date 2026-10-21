using System.Globalization;
using System.Text;
using CTC.Core.Models;

namespace CTC.Core.Scheduling;

/// <summary>Outcome of <see cref="ScheduleCsvImporter.Import"/>.</summary>
public sealed class ScheduleCsvImportResult
{
    private ScheduleCsvImportResult(ScheduleTemplate? template, string? errorMessage)
    {
        Template = template;
        ErrorMessage = errorMessage;
    }

    public bool IsSuccess => ErrorMessage is null;

    /// <summary>Dispatcher-readable reason the file was rejected; null on success.</summary>
    public string? ErrorMessage { get; }

    /// <summary>The filled template on success; null on failure.</summary>
    public ScheduleTemplate? Template { get; }

    internal static ScheduleCsvImportResult Success(ScheduleTemplate template) => new(template, null);

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
/// </remarks>
public static class ScheduleCsvImporter
{
    private static readonly string[] RequiredColumns = ["Line", "Section", "Block Number"];

    /// <summary>Accepted headers for the optional dwell column (matched ignoring case and spaces).</summary>
    private static readonly string[] DwellColumnNames = ["DwellTimeSeconds", "Dwell Time (sec)", "Dwell (sec)"];

    /// <param name="line">The line the schedule is for; the CSV must name this line.</param>
    /// <param name="csv">The file contents.</param>
    /// <param name="firstTrainNumber">First train number to use (see <see cref="TrainIds.NextAvailableNumber"/>).</param>
    public static ScheduleCsvImportResult Import(CtcLineState line, string csv, int firstTrainNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(csv);

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
            template = ScheduleTemplateFactory.Create(line, trainCount, firstTrainNumber);
        }
        catch (InvalidOperationException ex)
        {
            return ScheduleCsvImportResult.Failure(ex.Message);
        }

        string lineName = DisplayLineName(line);
        var timedBlockIds = new HashSet<string>();
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

        return ScheduleCsvImportResult.Success(template);
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
