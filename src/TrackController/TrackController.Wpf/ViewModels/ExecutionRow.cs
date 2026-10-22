using TrackController.Core.Models;
using TrackController.Runtime;

namespace TrackController.Wpf.ViewModels;

/// <summary>Stores draft PC/Pi placement separately from the currently applied deployment.</summary>
public sealed class ExecutionRow : ViewModelBase
{
    private string _target, _host, _port;

    public string Id { get; }

    public string Name { get; }

    public string Blocks { get; }

    public string AppliedTarget { get; }

    public string[] Targets { get; } = ["PC", "Pi"];

    public string Target
    {
        get => _target;
        set
        {
            if (value is not null && SetProperty(ref _target, value))
            {
                OnPropertyChanged(nameof(IsPi));
            }
        }
    }

    public bool IsPi => Target == "Pi";

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    /// <summary>Copies applied placement into independently editable UI fields.</summary>
    public ExecutionRow(PreparedDeployment.PreparedTerritory territory)
    {
        Id = territory.Id;
        Name = territory.Name;
        Blocks = string.Join(", ", WaysideConfiguration.GetOwnedIds(territory.Configuration));
        _target = AppliedTarget = territory.Execution == "Pi" ? "Pi" : "PC";
        _host = territory.Host;
        _port = territory.Port.ToString();
    }

    /// <summary>Validates draft connection fields and creates an execution assignment.</summary>
    public TerritoryAssignment ToAssignment()
    {
        if (Target is not ("PC" or "Pi") || !int.TryParse(Port, out int port))
        {
            throw new ArgumentException($"{Name}: choose PC/Pi and a numeric tunnel port.");
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
