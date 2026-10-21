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

    public List<TerritoryAssignment> Territories { get; set; } = [];

    /// <summary>Validates deployment files and creates the corresponding territory runtime router.</summary>
    public static TerritoryRuntimeRouter Load(string path) => Read(path).CreateRuntime();

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
        var prepared = new List<PreparedTerritory>();
        if (plan.TrackFile is not null || plan.TerritoriesFile is not null)
        {
            if (plan.TrackFile is null || plan.TerritoriesFile is null || plan.Territories.Any(assignment => assignment.ConfigurationFile is not null))
            {
                throw new ArgumentException("Use one trackFile and territoriesFile; do not mix shared and legacy territory definitions.");
            }

            var track = ReadConfiguration(Resolve(folder, plan.TrackFile));
            if (track.OwnedBlockIds is not null)
            {
                throw new ArgumentException("The shared track map describes all blocks; put ownership in the territories file.");
            }

            var ownership = JsonSerializer.Deserialize<TerritoryDefinitions>(ReadBounded(Resolve(folder, plan.TerritoriesFile), 1_048_576), Json) ?? throw new ArgumentException("Empty territory definitions.");
            if (ownership.Territories is null || ownership.Territories.Count is < 1 or > 16 || ownership.Territories.Any(territoryDefinition => territoryDefinition is null || string.IsNullOrWhiteSpace(territoryDefinition.Id) || territoryDefinition.BlockIds is null || territoryDefinition.BlockIds.Count == 0) || ownership.Territories.Select(territoryDefinition => territoryDefinition.Id).Distinct().Count() != ownership.Territories.Count)
            {
                throw new ArgumentException("Define 1–16 uniquely named territories, each with owned block IDs.");
            }

            var ids = ownership.Territories.SelectMany(territoryDefinition => territoryDefinition.BlockIds).ToArray();
            if (ids.Distinct().Count() != ids.Length || !ids.Order().SequenceEqual(track.Blocks.Select(blockDefinition => blockDefinition.BlockId).Order()))
            {
                throw new ArgumentException("Every shared-map block must have exactly one territory owner; check missing, unknown, or duplicate block IDs.");
            }

            if (plan.Territories.Any(assignment => string.IsNullOrWhiteSpace(assignment.TerritoryId)) || !plan.Territories.Select(assignment => assignment.TerritoryId).Order().SequenceEqual(ownership.Territories.Select(territoryDefinition => territoryDefinition.Id).Order()))
            {
                throw new ArgumentException("Provide exactly one execution assignment for every territory ID.");
            }

            foreach (var definition in ownership.Territories)
            {
                var assignment = plan.Territories.Single(assignment => assignment.TerritoryId == definition.Id);
                var configuration = WaysideConfiguration.ValidateAndCopy(track);
                configuration.WaysideId = definition.Id;
                configuration.OwnedBlockIds = [.. definition.BlockIds];
                configuration = WaysideConfiguration.ValidateAndCopy(configuration);
                prepared.Add(new(definition.Id, string.IsNullOrWhiteSpace(definition.Name) ? definition.Id : definition.Name, configuration, assignment.Execution, assignment.Host, assignment.Port));
            }
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

    internal static void ValidateTargets(IReadOnlyList<PreparedTerritory> territories)
    {
        if (territories.Count is < 1 or > 16 || territories.Select(territory => territory.Id).Distinct().Count() != territories.Count || territories.Select(territory => territory.Configuration.LineName).Distinct().Count() != 1)
        {
            throw new ArgumentException("Territories need unique IDs and must belong to one line.");
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
