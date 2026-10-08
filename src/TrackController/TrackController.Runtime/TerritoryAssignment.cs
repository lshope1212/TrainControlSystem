using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Control;
using TrackController.Core.Models;

namespace TrackController.Runtime;

/// <summary>Stores execution placement separately from the physical map and block ownership.</summary>
public sealed class TerritoryAssignment
{
    public string? TerritoryId { get; set; }

    public string? ConfigurationFile { get; set; }

    public string Execution { get; set; } = "Local";

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 11405;
}
