using System.Globalization;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using TrackModel.Core.Models;
namespace TrackModel.Core.Persistence;

/// <summary>Imports JSON and quoted CSV layouts, with SI units.</summary>
public sealed class TrackFileRepository : ITrackRepository
{
    public TrackLayout Load(string source)
    {
        TrackLayout layout;
        if (Path.GetExtension(source).Equals(".csv", StringComparison.OrdinalIgnoreCase)) layout = LoadCsv(source);
        else if (Path.GetExtension(source).Equals(".json", StringComparison.OrdinalIgnoreCase))
            layout = JsonSerializer.Deserialize<TrackLayout>(File.ReadAllText(source), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Layout file is empty.");
        else throw new InvalidDataException("Choose a JSON or CSV layout file.");
        TrackLayoutValidator.Validate(layout);
        return layout;
    }

    private static TrackLayout LoadCsv(string source)
    {
        using var reader = new TextFieldParser(source) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        reader.SetDelimiters(",");
        var headers = reader.ReadFields() ?? throw new InvalidDataException("CSV has no header.");
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
            throw new InvalidDataException("CSV column names must be unique.");
        foreach (var required in new[] { "Id", "LineId", "LengthMeters" })
            if (!headers.Contains(required, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("CSV needs column " + required);
        var layout = new TrackLayout { Name = Path.GetFileNameWithoutExtension(source) };
        while (!reader.EndOfData)
        {
            var fields = reader.ReadFields()!;
            if (fields.Length != headers.Length) throw new InvalidDataException($"CSV row near line {reader.LineNumber} has the wrong number of fields.");
            var row = headers.Zip(fields).ToDictionary(x => x.First, x => x.Second, StringComparer.OrdinalIgnoreCase);
            string S(string key, string fallback = "") => row.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;
            double D(string key, double fallback = 0) => double.Parse(S(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
            bool B(string key) => bool.Parse(S(key, "false"));
            int I(string key) => int.Parse(S(key, "0"), CultureInfo.InvariantCulture);
            layout.Blocks.Add(new TrackBlock { Id = S("Id"), LineId = S("LineId"), Number = I("Number"), Section = S("Section", "Main"),
                LengthMeters = D("LengthMeters"), ElevationMeters = D("ElevationMeters"), GradePercent = D("GradePercent"),
                TemperatureCelsius = D("TemperatureCelsius", 20), SpeedLimitMetersPerSecond = D("SpeedLimitMetersPerSecond", 19.444444),
                StationName = S("StationName"), InitialWaitingPassengers = I("InitialWaitingPassengers"),
                HasSwitch = B("HasSwitch"), HasSignal = B("HasSignal"), HasCrossing = B("HasCrossing"), HasHeater = B("HasHeater"),
                TravelDirection = S("TravelDirection", "Bidirectional"),
                ConnectedBlockIds = S("ConnectedBlockIds").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                NormalNextBlockId = S("NormalNextBlockId"), ReverseNextBlockId = S("ReverseNextBlockId") });
        }
        return layout;
    }
}
