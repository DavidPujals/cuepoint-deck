namespace SegmentDeck.Core.Resolume;

/// <summary>The slice of <see cref="ResolumeConnection"/> the trigger logic needs, so it can be tested without Resolume.</summary>
public interface IResolumeLink
{
    ConnectionState State { get; }
    bool CanSend { get; }
    ClipInfo? WatchedClip { get; }

    event Action<ConnectionState>? StateChanged;
    event Action<ClipInfo>? ClipStateChanged;
    event Action<PositionUpdate>? PositionUpdated;
    event Action<ClipInfo>? TransportChanged;

    Task SetPositionMsAsync(ClipInfo clip, double ms);
    Task ConnectClipAsync(ClipInfo clip);
    Task<int> SetInPointMsAsync(ClipInfo clip, double ms);
}
