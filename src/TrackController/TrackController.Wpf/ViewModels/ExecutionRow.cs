using TrackController.Core.Models;
using TrackController.Runtime;

namespace TrackController.Wpf.ViewModels;

/// <summary>Stores draft PC/Pi placement separately from the currently applied deployment.</summary>
public sealed class ExecutionRow : ViewModelBase
{
    private string _target, _host, _port;
    private readonly string _appliedHost, _appliedPort;

    public string Id { get; }

    public string Name { get; }
    public string Line { get; }

    public string Blocks { get; }

    public string AppliedTarget { get; }

    public string BlockSummary { get; }

    public bool HasChanges => Target != AppliedTarget || Host != _appliedHost || Port != _appliedPort;

    public string AssignmentSummary => HasChanges ? $"{Target} · edited" : Target;

    public string AppliedDescription => AppliedTarget == "Pi" ? $"Currently: Pi · {_appliedHost}:{_appliedPort}" : "Currently: PC";

    private void NotifyAssignment()
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(AssignmentSummary));
    }

    public string[] Targets { get; } = ["PC", "Pi"];

    public string Target
    {
        get => _target;
        set
        {
            if (value is not null && SetProperty(ref _target, value))
            {
                OnPropertyChanged(nameof(IsPi));
                NotifyAssignment();
            }
        }
    }

    public bool IsPi => Target == "Pi";

    public string Host
    {
        get => _host;
        set { if (SetProperty(ref _host, value)) NotifyAssignment(); }
    }

    public string Port
    {
        get => _port;
        set { if (SetProperty(ref _port, value)) NotifyAssignment(); }
    }

    /// <summary>Copies applied placement into independently editable UI fields.</summary>
    public ExecutionRow(PreparedDeployment.PreparedTerritory territory)
    {
        Id = territory.Id;
        Name = territory.Name;
        Line = territory.Configuration.LineName;
        var blocks = WaysideConfiguration.GetOwnedIds(territory.Configuration);
        Blocks = string.Join(", ", blocks);
        BlockSummary = $"{Line} · {blocks.Count} blocks";
        _target = AppliedTarget = territory.Execution == "Pi" ? "Pi" : "PC";
        _host = _appliedHost = territory.Host;
        _port = _appliedPort = territory.Port.ToString();
    }

    /// <summary>Validates draft connection fields and creates an execution assignment.</summary>
    public TerritoryAssignment ToAssignment()
    {
        if (Target is not ("PC" or "Pi") || !int.TryParse(Port, out int port))
        {
            throw new ArgumentException($"{Name}: choose PC/Pi and a numeric TCP port.");
        }

        return new()
        {
            TerritoryId = Id,
            Execution = IsPi ? "Pi" : "Local",
            Host = Host,
            Port = port
        };
    }
}
