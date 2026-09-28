using TrackModel.Core.Interfaces;
using TrackModel.Core.Models;

namespace TrackModel.Core.Services;

/// <summary>
/// Stub implementation of <see cref="ITrackService"/>. Holds an empty layout for now.
/// </summary>
public class TrackService : ITrackService
{
    public TrackLayout Layout { get; } = new TrackLayout { Name = "Placeholder Layout" };

    public TrackBlock? FindBlock(string blockId) =>
        Layout.Blocks.FirstOrDefault(b => b.Id == blockId);
}
