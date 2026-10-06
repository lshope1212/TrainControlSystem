using System.Text.Json;
using TrackController.Core.Control;
using TrackController.Core.Services;

namespace TrackController.Runtime;

/// <summary>Validated map/ownership plus replaceable execution choices. Reading/editing never starts a controller.</summary>
public sealed class PreparedDeployment
{
    private string _source;
    private readonly WaysideDeployment _document;
    public string SourcePath
    {
        get;
    }
    public string Name => _document.Name;
    public string LayoutDescription => _document.TrackFile is null ? "Legacy territory files" : $"Map: {_document.TrackFile} Â· Ownership: {_document.TerritoriesFile}";
    public IReadOnlyList<PreparedTerritory> Territories
    {
        get;
    }
    public string ProgramName
    {
        get;
    }
    public string ProgramSource
    {
        get;
    }

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
            throw new ArgumentException("Provide exactly one execution choice per territory.");
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

    /// <summary>Creates a deployment carrying replacement PLC source for the next runtime session.</summary>
    public PreparedDeployment WithProgram(string name, string source)
    {
        BooleanPlcProgram.Compile(source);
        return new(SourcePath, _source, _document, Territories, name, source);
    }

    /// <summary>Explicit save of execution placement only; never rewrites topology, ownership, or PLC source.</summary>
    public void SaveExecution()
    {
        if (File.ReadAllText(SourcePath) != _source)
            throw new IOException("Deployment file changed on disk. Reload it before saving execution choices.");
        string text = JsonSerializer.Serialize(_document, WaysideDeployment.Json) + Environment.NewLine;
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
                File.Delete(temporary);
        }
    }

    /// <summary>Creates the territory runtimes and disposes partially created instances if setup fails.</summary>
    public TerritoryRuntimeRouter CreateRuntime()
    {
        WaysideDeployment.ValidateTargets(Territories);
        BooleanPlcProgram.Compile(ProgramSource);
        var entries = new List<TerritoryRuntimeRouter.Entry>();
        try
        {
            foreach (var territory in Territories)
            {
                IWaysideRuntime runtime;
                if (territory.Execution == "Pi")
                    runtime = new PiWaysideRuntime(territory.Host, territory.Port, territory.Configuration, ProgramName, ProgramSource);
                else
                {
                    var service = new TrackControllerService(territory.Configuration);
                    service.LoadPlc(ProgramName, ProgramSource);
                    runtime = new LocalWaysideRuntime(service);
                }

                entries.Add(new(territory.Configuration, territory.Execution, runtime, territory.Name));
            }

            return new(Name, entries)
            {
                InitialProgramSource = ProgramSource
            };
        }
        catch
        {
            foreach (var entry in entries)
                entry.Runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }
}
