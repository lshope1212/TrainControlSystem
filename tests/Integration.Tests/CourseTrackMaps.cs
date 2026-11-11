using System.Text.Json;
using System.Text.Json.Serialization;
using TrackController.Contracts.Messages;
using TrackController.Runtime;
using TrackModel.Core.Models;

namespace TrainControl.Integration.Tests;

/// <summary>
/// Derives a Track Controller shared track map from the Track Model's layout, the authority for
/// block identity and physical layout. This is how <c>red-line-track-map.json</c> and
/// <c>green-line-track-map.json</c> (and their single-territory ownership files) were produced, and
/// the tests check the checked-in files still equal what it derives, so the maps cannot drift from
/// the Track Model. Set <c>TRAINCONTROL_REGENERATE_TRACK_MAPS=1</c> and run
/// <see cref="CourseNetworkDataTests"/> to rewrite them in the source tree.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>IDs, lengths, speed limits, stations, switches and crossings are the Track Model's.</item>
///   <item>Exits (<c>nextBlockIds</c>) are the Track Model connections the travel direction permits:
///   a one-way block (<c>Forward</c>: increasing numbers, <c>Reverse</c>: decreasing) is entered only
///   from its upstream neighbour and left only toward its downstream one; a bidirectional block
///   both ways.</item>
///   <item>A switch lists both legs as exits even when one leg is entered only from the switch's
///   far side, because the controller's switch model requires it; the one-way leg block itself has
///   no exit back, so no route can continue against the direction of travel.</item>
///   <item>Protection zones follow the Blue Line map: a switch with its stem and both legs; a
///   crossing with its adjacent blocks.</item>
///   <item>Map coordinates are presentation only: rows of <see cref="BlocksPerRow"/> blocks in block
///   order, each line in its own band below the Blue Line's drawing.</item>
/// </list>
/// </remarks>
internal static class CourseTrackMaps
{
    public const int BlocksPerRow = 20;

    /// <summary>Top of each line's band on the shared Track Controller canvas (the Blue Line map uses y 60–260).</summary>
    public static readonly IReadOnlyDictionary<string, double> BandTop = new Dictionary<string, double> { ["Red"] = 420, ["Green"] = 950 };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string MapFile(string lineId) => $"{lineId.ToLowerInvariant()}-line-track-map.json";

    public static string TerritoriesFile(string lineId) => $"{lineId.ToLowerInvariant()}-line-territories-single.json";

    public static string TerritoryId(string lineId) => "wayside-" + lineId.ToLowerInvariant();

    public static WaysideConfigurationMessage BuildMap(TrackLayout layout, string lineId)
    {
        var blocks = layout.Blocks.Where(block => block.LineId == lineId).OrderBy(block => block.Number).ToList();
        var byId = blocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var byNumber = blocks.ToDictionary(block => block.Number);

        // Upstream/downstream neighbours of a one-way block: the next number in its direction is
        // downstream, the previous one upstream; a switch branch fills whichever role is missing.
        (string Upstream, string Downstream) Flow(TrackBlock block)
        {
            int step = block.TravelDirection == "Forward" ? 1 : -1;
            string? Sequential(int number) => byNumber.TryGetValue(number, out var other) && block.ConnectedBlockIds.Contains(other.Id) ? other.Id : null;
            string? down = Sequential(block.Number + step), up = Sequential(block.Number - step);
            var others = block.ConnectedBlockIds.Except(new[] { down, up }.OfType<string>()).ToList();
            if (others.Count != (down is null ? 1 : 0) + (up is null ? 1 : 0))
            {
                throw new InvalidOperationException($"One-way block {block.Id} has no unambiguous upstream and downstream neighbour.");
            }

            down ??= others[0];
            up ??= others.Single(id => id != down);
            return (up, down);
        }

        IEnumerable<string> Exits(TrackBlock block) => block.TravelDirection == "Bidirectional" ? block.ConnectedBlockIds : [Flow(block).Downstream];
        IEnumerable<string> Entries(TrackBlock block) => block.TravelDirection == "Bidirectional" ? block.ConnectedBlockIds : [Flow(block).Upstream];
        IEnumerable<string> InNumberOrder(IEnumerable<string> ids) => ids.Distinct().OrderBy(id => byId[id].Number);

        var definitions = new List<WaysideBlockDefinition>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var next = Exits(block).Where(id => Entries(byId[id]).Contains(block.Id)).ToList();
            var definition = new WaysideBlockDefinition
            {
                BlockId = block.Id,
                LengthMeters = block.LengthMeters,
                SpeedLimitMetersPerSecond = block.SpeedLimitMetersPerSecond,
                HasSignal = block.HasSignal,
                HasCrossing = block.HasCrossing,
                StationName = block.StationName.Length > 0 ? block.StationName : null,
                MapX = 30 + i % BlocksPerRow * 106,
                MapY = BandTop[lineId] + i / BlocksPerRow * 110,
            };
            if (block.HasSwitch)
            {
                foreach (string leg in new[] { block.NormalNextBlockId, block.ReverseNextBlockId }.Where(leg => !next.Contains(leg)))
                {
                    next.Add(leg);
                }
                definition.NormalNextBlockId = block.NormalNextBlockId;
                definition.ReverseNextBlockId = block.ReverseNextBlockId;
                definition.StemBlockId = block.ConnectedBlockIds.Except([block.NormalNextBlockId, block.ReverseNextBlockId]).Single();
                definition.SwitchProtectionBlockIds = [.. InNumberOrder([definition.StemBlockId, block.Id, block.NormalNextBlockId, block.ReverseNextBlockId])];
            }

            if (block.HasCrossing)
            {
                definition.CrossingProtectionBlockIds = [.. InNumberOrder(block.ConnectedBlockIds.Append(block.Id))];
            }

            definition.NextBlockIds = [.. InNumberOrder(next)];
            definitions.Add(definition);
        }

        return new WaysideConfigurationMessage
        {
            WaysideId = "Shared track map",
            LineName = lineId + " Line",
            FeedbackTimeoutSeconds = 5,
            RequestTimeoutSeconds = 10,
            Blocks = definitions,
        };
    }

    public static TerritoryDefinitions BuildSingleTerritory(TrackLayout layout, string lineId) => new()
    {
        Territories =
        [
            new TerritoryDefinition
            {
                Id = TerritoryId(lineId),
                Name = lineId + " Line",
                BlockIds = [.. layout.Blocks.Where(block => block.LineId == lineId).OrderBy(block => block.Number).Select(block => block.Id)],
            },
        ],
    };

    /// <summary>Canonical text of a JSON file in this format, so formatting differences never matter.</summary>
    public static string Canonical<T>(string json) => JsonSerializer.Serialize(JsonSerializer.Deserialize<T>(json, Json), Json);

    /// <summary>The repository's Track Controller samples folder (for regeneration), found from the test's output folder.</summary>
    public static string SourceSamplesFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "TrainControlSystem.sln")))
            {
                return Path.Combine(folder.FullName, "src", "TrackController", "TrackController.Wpf", "Samples");
            }
        }

        throw new DirectoryNotFoundException("Repository root (TrainControlSystem.sln) not found above the test output folder.");
    }
}
