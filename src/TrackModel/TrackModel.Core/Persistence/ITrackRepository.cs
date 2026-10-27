using TrackModel.Core.Models;

namespace TrackModel.Core.Persistence;

/// <summary>
/// Loads a validated static track layout from a file.
/// </summary>
public interface ITrackRepository
{
    TrackLayout Load(string source);
}
