using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Control;
using TrackController.Core.Models;

namespace TrackController.Runtime;

/// <summary>Execution choices reference territory IDs; topology and ownership live in separate files.</summary>
public sealed class WaysideDeployment
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Name { get; set; } = "Wayside deployment";

    public string PlcFile { get; set; } = "wayside.plc";

    public string? TrackFile { get; set; }

    public string? TerritoriesFile { get; set; }

    /// <summary>
    /// Several lines in one deployment, each with its own shared track map and ownership file,
    /// instead of <see cref="TrackFile"/>/<see cref="TerritoriesFile"/>. Block IDs must be unique
    /// across every line, because the module messages identify a block without its line.
    /// </summary>
    public List<WaysideLineFiles>? Lines { get; set; }

    public List<TerritoryAssignment> Territories { get; set; } = [];

    /// <summary>Loads and validates deployment files before any controller connection is opened.</summary>
    public static PreparedDeployment Read(string path)
    {
        path = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(path)!;
        string source = ReadBounded(path, 1_048_576);
        var plan = JsonSerializer.Deserialize<WaysideDeployment>(source, Json) ?? throw new ArgumentException("Empty deployment.");
        if (string.IsNullOrWhiteSpace(plan.Name) || plan.Territories is null || plan.Territories.Count is < 1 or > 16 || plan.Territories.Any(assignment => assignment is null))
        {
            throw new ArgumentException("Name the deployment and provide 1–16 territory assignments.");
        }

        string plc = ReadBounded(Resolve(folder, plan.PlcFile), VitalPlcProgram.MaximumSourceBytes);
        VitalPlcProgram.Compile(plc);
        var prepared = new List<PreparedDeployment.PreparedTerritory>();
        if (plan.Lines is not null)
        {
            if (plan.TrackFile is not null || plan.TerritoriesFile is not null || plan.Territories.Any(assignment => assignment.ConfigurationFile is not null))
            {
                throw new ArgumentException("Use either lines or one trackFile and territoriesFile; do not mix them or legacy territory definitions.");
            }

            if (plan.Lines.Count is < 1 or > 16 || plan.Lines.Any(line => line is null || string.IsNullOrWhiteSpace(line.TrackFile) || string.IsNullOrWhiteSpace(line.TerritoriesFile)))
            {
                throw new ArgumentException("Each of the 1–16 lines needs a trackFile and a territoriesFile.");
            }

            var lineOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var lineNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var definitions = new List<(TerritoryDefinition Definition, WaysideConfigurationMessage Track)>();
            foreach (var line in plan.Lines)
            {
                var (track, ownership) = ReadSharedMap(folder, line.TrackFile!, line.TerritoriesFile!);
                if (!lineNames.Add(track.LineName))
                {
                    throw new ArgumentException($"{track.LineName} appears in more than one line of the deployment.");
                }

                foreach (var block in track.Blocks)
                {
                    if (!lineOf.TryAdd(block.BlockId, track.LineName))
                    {
                        throw new ArgumentException($"Block ID '{block.BlockId}' is on both {lineOf[block.BlockId]} and {track.LineName}. Block IDs must be unique across every line because module messages carry no line; use the Track Model's IDs (e.g. \"RED-12\").");
                    }
                }

                definitions.AddRange(ownership.Territories.Select(definition => (definition, track)));
            }

            prepared.AddRange(PrepareTerritories(plan, definitions));
        }
        else if (plan.TrackFile is not null || plan.TerritoriesFile is not null)
        {
            if (plan.TrackFile is null || plan.TerritoriesFile is null || plan.Territories.Any(assignment => assignment.ConfigurationFile is not null))
            {
                throw new ArgumentException("Use one trackFile and territoriesFile; do not mix shared and legacy territory definitions.");
            }

            var (track, ownership) = ReadSharedMap(folder, plan.TrackFile, plan.TerritoriesFile);
            prepared.AddRange(PrepareTerritories(plan, ownership.Territories.Select(definition => (definition, track))));
        }
        else
        {
            foreach (var assignment in plan.Territories)
            {
                var config = ReadConfiguration(Resolve(folder, assignment.ConfigurationFile ?? ""));
                assignment.TerritoryId = config.WaysideId;
                prepared.Add(new(config.WaysideId, config.WaysideId, config, assignment.Execution, assignment.Host, assignment.Port));
            }
        }

        for (int i = 0; i < prepared.Count; i++)
        {
            var assignment = plan.Territories.Single(item => item.TerritoryId == prepared[i].Id);
            if (assignment.PlcFile is { } file)
            {
                string territorySource = ReadBounded(Resolve(folder, file), VitalPlcProgram.MaximumSourceBytes);
                VitalPlcProgram.Compile(territorySource);
                prepared[i] = prepared[i] with { ProgramName = Path.GetFileName(file), ProgramSource = territorySource };
            }
        }

        ValidateTargets(prepared);
        return new(path, source, plan, prepared, Path.GetFileName(plan.PlcFile), plc);
    }

    /// <summary>Reads one line's shared track map and the territories that own its blocks; every map block needs exactly one owner.</summary>
    private static (WaysideConfigurationMessage Track, TerritoryDefinitions Ownership) ReadSharedMap(string folder, string trackFile, string territoriesFile)
    {
        var track = ReadConfiguration(Resolve(folder, trackFile));
        if (track.OwnedBlockIds is not null)
        {
            throw new ArgumentException("The shared track map describes all blocks; put ownership in the territories file.");
        }

        var ownership = JsonSerializer.Deserialize<TerritoryDefinitions>(ReadBounded(Resolve(folder, territoriesFile), 1_048_576), Json) ?? throw new ArgumentException("Empty territory definitions.");
        if (ownership.Territories is null || ownership.Territories.Count is < 1 or > 16 || ownership.Territories.Any(territoryDefinition => territoryDefinition is null || string.IsNullOrWhiteSpace(territoryDefinition.Id) || territoryDefinition.BlockIds is null || territoryDefinition.BlockIds.Count == 0) || ownership.Territories.Select(territoryDefinition => territoryDefinition.Id).Distinct().Count() != ownership.Territories.Count)
        {
            throw new ArgumentException("Define 1–16 uniquely named territories, each with owned block IDs.");
        }

        var ids = ownership.Territories.SelectMany(territoryDefinition => territoryDefinition.BlockIds).ToArray();
        if (ids.Distinct().Count() != ids.Length || !ids.Order().SequenceEqual(track.Blocks.Select(blockDefinition => blockDefinition.BlockId).Order()))
        {
            throw new ArgumentException($"Every shared-map block{(string.IsNullOrWhiteSpace(track.LineName) ? "" : $" of {track.LineName}")} must have exactly one territory owner; check missing, unknown, or duplicate block IDs.");
        }

        return (track, ownership);
    }

    /// <summary>One validated controller configuration per territory: its line's full map plus the blocks it alone may command.</summary>
    private static IEnumerable<PreparedDeployment.PreparedTerritory> PrepareTerritories(WaysideDeployment plan, IEnumerable<(TerritoryDefinition Definition, WaysideConfigurationMessage Track)> territories)
    {
        var all = territories.ToList();
        if (all.Count is < 1 or > 16 || all.Select(territory => territory.Definition.Id).Distinct().Count() != all.Count)
        {
            throw new ArgumentException("A deployment has 1–16 territories with unique IDs across all of its lines.");
        }

        if (plan.Territories.Any(assignment => string.IsNullOrWhiteSpace(assignment.TerritoryId)) || !plan.Territories.Select(assignment => assignment.TerritoryId).Order().SequenceEqual(all.Select(territory => territory.Definition.Id).Order()))
        {
            throw new ArgumentException("Provide exactly one execution assignment for every territory ID.");
        }

        foreach (var (definition, track) in all)
        {
            var assignment = plan.Territories.Single(assignment => assignment.TerritoryId == definition.Id);
            var configuration = WaysideConfiguration.ValidateAndCopy(track);
            configuration.WaysideId = definition.Id;
            configuration.OwnedBlockIds = [.. definition.BlockIds];
            configuration = WaysideConfiguration.ValidateAndCopy(configuration);
            yield return new(definition.Id, string.IsNullOrWhiteSpace(definition.Name) ? definition.Id : definition.Name, configuration, assignment.Execution, assignment.Host, assignment.Port);
        }
    }

    internal static void ValidateTargets(IReadOnlyList<PreparedDeployment.PreparedTerritory> territories)
    {
        if (territories.Count is < 1 or > 16 || territories.Select(territory => territory.Id).Distinct().Count() != territories.Count)
        {
            throw new ArgumentException("Territories need unique IDs.");
        }

        // A deployment may span several lines; a territory belongs to exactly one, and no block ID may exist on two.
        var lineOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in territories.GroupBy(territory => territory.Configuration.LineName))
        {
            foreach (string id in line.SelectMany(territory => territory.Configuration.Blocks.Select(block => block.BlockId)).Distinct())
            {
                if (!lineOf.TryAdd(id, line.Key))
                {
                    throw new ArgumentException($"Block ID '{id}' is on both {lineOf[id]} and {line.Key}; block IDs must be unique across lines.");
                }
            }
        }

        var ids = territories.SelectMany(territory => WaysideConfiguration.GetOwnedIds(territory.Configuration)).ToArray();
        if (ids.Distinct().Count() != ids.Length)
        {
            throw new ArgumentException("Each block must have exactly one owner.");
        }

        if (!ids.Order().SequenceEqual(territories.SelectMany(territory => territory.Configuration.Blocks.Select(blockDefinition => blockDefinition.BlockId)).Distinct().Order()))
        {
            throw new ArgumentException("Every block referenced by the deployment must have an owner.");
        }

        foreach (var territory in territories)
        {
            if (territory.Execution is not ("Local" or "Pi"))
            {
                throw new ArgumentException("Execution must be Local or Pi.");
            }

            if (territory.Execution == "Pi" && (!IPAddress.TryParse(territory.Host, out var address) || !IPAddress.IsLoopback(address) || territory.Port is < 1 or > 65535))
            {
                throw new ArgumentException($"{territory.Name}: use a loopback IP and valid SSH tunnel port for the Pi.");
            }
        }

        var ports = territories.Where(territory => territory.Execution == "Pi").Select(territory => territory.Port).ToArray();
        if (ports.Distinct().Count() != ports.Length)
        {
            throw new ArgumentException("Each Pi territory needs a distinct local tunnel/runtime port; one runtime has one owner.");
        }
    }

    private static WaysideConfigurationMessage ReadConfiguration(string path) => WaysideConfiguration.ValidateAndCopy(JsonSerializer.Deserialize<WaysideConfigurationMessage>(ReadBounded(path, 1_048_576), Json) ?? throw new ArgumentException("Empty track configuration."));

    private static string Resolve(string folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new ArgumentException("Deployment files must use relative paths beside the deployment JSON.");
        }

        string path = Path.GetFullPath(Path.Combine(folder, relative));
        if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Deployment file path leaves its folder.");
        }

        return path;
    }

    private static string ReadBounded(string path, long bytes)
    {
        if (new FileInfo(path).Length > bytes)
        {
            throw new ArgumentException($"File is too large: {Path.GetFileName(path)}");
        }

        return File.ReadAllText(path);
    }
}
