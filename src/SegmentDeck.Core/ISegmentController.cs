using SegmentDeck.Core.Library;

namespace SegmentDeck.Core;

/// <summary>The one place segment triggers go through. The UI, keyboard, and later ProPresenter follow or a
/// Companion endpoint all call this; none of them touch Resolume directly.</summary>
public interface ISegmentController
{
    /// <summary>Seek straight to the segment's start.</summary>
    Task CutAsync(Song song, Segment segment);

    /// <summary>Wait until the current segment ends, then seek. Replaces any queued segment.</summary>
    Task QueueAsync(Song song, Segment segment);

    void ClearQueue();

    /// <summary>Connect the song's clip on the song layer (only when "Launch songs from setlist" is on).</summary>
    Task LaunchSongAsync(Song song);
}
