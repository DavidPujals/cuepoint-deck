using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SegmentDeck.Core.Library;
using SegmentDeck.Core.Playback;

namespace SegmentDeck.App.ViewModels;

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
    public string StartText => SegmentController.Fmt(Segment.StartMs);
    public BitmapImage? Thumb { get; }
    public bool HasThumb => Thumb is not null;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private bool _isNext;
    [ObservableProperty] private bool _isQueued;
    [ObservableProperty] private string _queueText = "";
    [ObservableProperty] private bool _isFlashing;

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
}
