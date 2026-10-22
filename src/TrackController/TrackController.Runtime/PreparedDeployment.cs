using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Control;
using TrackController.Core.Services;

namespace TrackController.Runtime;

/// <summary>Validated map/ownership plus replaceable execution choices. Reading/editing never starts a controller.</summary>
public sealed class PreparedDeployment
{
    /// <summary>Validated territory data used to construct its runtime; this record does not execute a controller.</summary>
    public sealed record PreparedTerritory(
        string Id,
        string Name,
        WaysideConfigurationMessage Configuration,
        string Execution,
        string Host,
        int Port)
    {
        public string? ProgramName { get; init; }

        public string? ProgramSource { get; init; }
    }

    private string _source;
    private readonly WaysideDeployment _document;

    public string SourcePath { get; }

    public string Name => _document.Name;

    public string LayoutDescription => _document.TrackFile is null ? "Legacy territory files" : $"Map: {_document.TrackFile} · Ownership: {_document.TerritoriesFile}";

    public IReadOnlyList<PreparedTerritory> Territories { get; }

    public string ProgramName { get; }

    public string ProgramSource { get; }

    internal PreparedDeployment(string path, string source, WaysideDeployment document, IReadOnlyList<PreparedTerritory> territories, string programName, string programSource)
    {
        SourcePath = path;
        _source = source;
        _document = document;
        Territories = territories;
        ProgramName = programName;
        ProgramSource = programSource;
    }

    /// <summary>Creates a deployment with new placement while preserving topology, ownership, and PLC source.</summary>
    public PreparedDeployment WithExecution(IEnumerable<TerritoryAssignment> settings)
    {
        var choices = settings.ToArray();
        if (choices.Any(assignment => assignment is null) || !choices.Select(assignment => assignment.TerritoryId).Order().SequenceEqual(Territories.Select(territory => territory.Id).Order()))
        {
            throw new ArgumentException("Provide exactly one execution choice per territory.");
        }

        var territories = Territories.Select(territory =>
                {
                    var choice = choices.Single(assignment => assignment.TerritoryId == territory.Id);
                    return territory with
                    {
                        Execution = choice.Execution,
                        Host = choice.Host,
                        Port = choice.Port
                    };
                }).ToArray();
        WaysideDeployment.ValidateTargets(territories);
        var document = JsonSerializer.Deserialize<WaysideDeployment>(JsonSerializer.Serialize(_document, WaysideDeployment.Json), WaysideDeployment.Json)!;
        foreach (var assignment in document.Territories)
        {
            var choice = choices.Single(candidate => candidate.TerritoryId == assignment.TerritoryId);
            assignment.Execution = choice.Execution;
            assignment.Host = choice.Host;
            assignment.Port = choice.Port;
        }

        return new(SourcePath, _source, document, territories, ProgramName, ProgramSource);
    }

    /// <summary>Assigns validated PLC source to one territory without changing other assignments.</summary>
    public PreparedDeployment WithTerritoryProgram(string territoryId, string name, string source)
    {
        if (!Territories.Any(territory => territory.Id == territoryId))
        {
            throw new ArgumentException("Unknown PLC territory.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(source) > VitalPlcProgram.MaximumSourceBytes)
        {
            throw new ArgumentException("PLC file exceeds 256 KiB.");
        }

        VitalPlcProgram.Compile(source);
        var territories = Territories.Select(territory => territory.Id == territoryId
            ? territory with { ProgramName = Path.GetFileName(name), ProgramSource = source }
            : territory).ToArray();
        return new(SourcePath, _source, _document, territories, ProgramName, ProgramSource);
    }

    /// <summary>Saves placement and PLC assignments without rewriting topology or original PLC files.</summary>
    public void SaveExecution()
    {
        if (File.ReadAllText(SourcePath) != _source)
        {
            throw new IOException("Deployment file changed on disk. Reload it before saving execution choices.");
        }

        var document = JsonSerializer.Deserialize<WaysideDeployment>(JsonSerializer.Serialize(_document, WaysideDeployment.Json), WaysideDeployment.Json)!;
        foreach (var territory in Territories.Where(territory => territory.ProgramSource is not null))
        {
            // Content-addressed copies preserve original files and keep saved setups portable.
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(territory.ProgramSource!)));
            string file = $"assigned-{hash}.plc";
            string destination = Path.Combine(Path.GetDirectoryName(SourcePath)!, file);
            if (!File.Exists(destination))
            {
                File.WriteAllText(destination, territory.ProgramSource);
            }
            else if (File.ReadAllText(destination) != territory.ProgramSource)
            {
                throw new IOException("Saved PLC copy has changed on disk.");
            }
            document.Territories.Single(assignment => assignment.TerritoryId == territory.Id).PlcFile = file;
        }

        string text = JsonSerializer.Serialize(document, WaysideDeployment.Json) + Environment.NewLine;
        string temporary = SourcePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, SourcePath, overwrite: true);
            _source = text;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Creates the territory runtimes and disposes partially created instances if setup fails.</summary>
    public TerritoryRuntimeRouter CreateRuntime()
    {
        WaysideDeployment.ValidateTargets(Territories);
        foreach (string source in Territories.Select(territory => territory.ProgramSource ?? ProgramSource).Distinct(StringComparer.Ordinal))
        {
            VitalPlcProgram.Compile(source);
        }
        var entries = new List<(WaysideConfigurationMessage Configuration, string Execution, IWaysideRuntime Runtime, string? Name)>();
        try
        {
            foreach (var territory in Territories)
            {
                IWaysideRuntime runtime;
                if (territory.Execution == "Pi")
                {
                    runtime = new PiWaysideRuntime(territory.Host, territory.Port, territory.Configuration, territory.ProgramName ?? ProgramName, territory.ProgramSource ?? ProgramSource);
                }
                else
                {
                    var service = new TrackControllerService(territory.Configuration);
                    service.LoadPlc(territory.ProgramName ?? ProgramName, territory.ProgramSource ?? ProgramSource);
                    runtime = new LocalWaysideRuntime(service);
                }

                entries.Add((territory.Configuration, territory.Execution, runtime, territory.Name));
            }

            return new(Name, entries);
        }
        catch
        {
            foreach (var entry in entries)
            {
                entry.Runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            throw;
        }
    }
}
