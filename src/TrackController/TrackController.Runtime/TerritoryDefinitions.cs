using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Control;
using TrackController.Core.Models;

namespace TrackController.Runtime;

/// <summary>Contains the named ownership territories associated with a shared track map.</summary>
public sealed class TerritoryDefinitions
{
    public List<TerritoryDefinition> Territories { get; set; } = [];
}
