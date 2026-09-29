using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CuepointDeck.Core;
using CuepointDeck.Core.Library;
using CuepointDeck.Core.Playback;

namespace CuepointDeck.App.ViewModels;

/// <summary>One segment card in Show mode.</summary>
public partial class SegmentCardViewModel : ObservableObject
{
    private readonly ShowViewModel _owner;

    public Song Song { get; }
    public Segment Segment { get; }
    public int Index { get; }
    public string NumberLabel { get; }
    public string Name => Segment.Name;
    public string Lyric => Segment.Lyric;
    public string ColorHex => Segment.Color;
    public string StartText => Timecode.Format(Segment.StartMs, Song.Fps);
    public string RangeText => Segment.EndMs is long e ? $"{StartText} → {Timecode.Format(e, Song.Fps)}" : StartText;
    public BitmapImage? Thumb { get; }
    public bool HasThumb => Thumb is not null;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private bool _isNext;
    [ObservableProperty] private bool _isQueued;
    [ObservableProperty] private string _queueText = "";
    [ObservableProperty] private bool _isFlashing;
    /// <summary>The loop is set on this segment (it repeats at its end whenever it is live).</summary>
    [ObservableProperty] private bool _isLooping;

    public SegmentCardViewModel(ShowViewModel owner, Song song, Segment segment, int index, BitmapImage? thumb)
    {
        _owner = owner;
        Song = song;
        Segment = segment;
        Index = index;
        NumberLabel = index < 9 ? (index + 1).ToString() : index == 9 ? "0" : "";
        Thumb = thumb;
    }

    [RelayCommand] private Task Cut() => _owner.FireAsync(this, Core.Settings.TriggerMode.Cut);
    [RelayCommand] private Task Queue() => _owner.FireAsync(this, Core.Settings.TriggerMode.Queue);
    [RelayCommand] private void ToggleLoop() => _owner.ToggleLoop(this);
}
