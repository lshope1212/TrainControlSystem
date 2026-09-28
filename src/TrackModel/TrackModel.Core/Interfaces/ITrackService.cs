using TrackModel.Core.Models;

namespace TrackModel.Core.Interfaces;

/// <summary>
/// Placeholder contract for querying the track layout.
/// </summary>
public interface ITrackService
{
    TrackLayout Layout { get; }

    TrackBlock? FindBlock(string blockId);
}
