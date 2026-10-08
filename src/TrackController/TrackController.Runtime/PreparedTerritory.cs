using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Control;
using TrackController.Core.Models;

namespace TrackController.Runtime;

/// <summary>Holds validated ownership, topology, and connection settings for runtime construction.</summary>
public sealed record PreparedTerritory(
    string Id,
    string Name,
    WaysideConfigurationMessage Configuration,
    string Execution,
    string Host,
    int Port);
