using System.Text;
using TrackController.Contracts.Messages;
using TrainControl.Common.Communication;

namespace TrackController.Runtime;

/// <summary>Identifies the Pi protocol version and clock before a controller session is initialized.</summary>
public sealed record PiHello(int Version, DateTimeOffset ServerTimeUtc);
