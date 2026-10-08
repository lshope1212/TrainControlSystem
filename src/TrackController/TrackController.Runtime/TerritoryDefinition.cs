using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Control;
using TrackController.Core.Models;

namespace TrackController.Runtime;

/// <summary>Names a territory and the block IDs that it alone may command.</summary>
public sealed class TerritoryDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public List<string> BlockIds { get; set; } = [];
}
