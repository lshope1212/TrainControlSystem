using TrackModel.Core.Models;

namespace TrackModel.Core.Persistence;

/// <summary>
/// Placeholder interface marking where track layout loading will eventually live
/// (for example, importing the provided track spreadsheet).
/// No implementation and no database exist yet.
/// </summary>
public interface ITrackRepository
{
    TrackLayout Load(string source);
}
