using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CuepointDeck.Core;
using CuepointDeck.Core.Library;
using CuepointDeck.Core.Resolume;

namespace CuepointDeck.App.ViewModels;

/// <summary>A song in the export list, ticked or not.</summary>
public partial class ExportSongItem : ObservableObject
{
    public required Song Song { get; init; }
    public string Title => Song.Title;
    public string Detail { get; init; } = "";
    [ObservableProperty] private bool _isChecked;
}

/// <summary>A choice in an import row's column picker: a column on the song layer, or not linked.</summary>
public sealed class ColumnChoice
{
    public ClipInfo? Clip { get; init; }
    public required string Label { get; init; }
    /// <summary>"Keep current link": an existing song stays on whatever it plays from now.</summary>
    public bool KeepsLink { get; init; }
    public override string ToString() => Label;
}

/// <summary>One song in an import file, with what to do with it and the column to link it to.</summary>
public partial class ImportSongItem : ObservableObject
{
    public required TransferSong Incoming { get; init; }
    public Song? Existing { get; init; }
    public bool IsExisting => Existing is not null;
    public string Title => Incoming.Title;
    public required ObservableCollection<ColumnChoice> Columns { get; init; }

    /// <summary>"8 segments · 00:05:00:00 · New", or "… · Already here" (with the local title when it differs).</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string> { Incoming.Segments.Count == 1 ? "1 segment" : $"{Incoming.Segments.Count} segments" };
            if (Incoming.DurationMs > 0) parts.Add(Timecode.Format(Incoming.DurationMs, Incoming.Fps > 0 ? Incoming.Fps : Timecode.DefaultFps));
            parts.Add(Existing is null ? "New here" : string.Equals(SongTransfer.NormalizeTitle(Existing.Title), SongTransfer.NormalizeTitle(Incoming.Title))
                ? $"Already here ({ExistingSegmentsText})" : $"Already here as “{Existing.Title}” ({ExistingSegmentsText})");
            return string.Join("  ·  ", parts);
        }
    }

    private string ExistingSegmentsText => Existing!.Segments.Count == 0 ? "no segments yet" : Existing.Segments.Count == 1 ? "1 segment" : $"{Existing.Segments.Count} segments";

    /// <summary>Import it (add, or replace the song already here) or leave it out.</summary>
    [ObservableProperty] private bool _include = true;
    public bool IsSkipped => !Include;
    partial void OnIncludeChanged(bool value) { OnPropertyChanged(nameof(IsSkipped)); Changed?.Invoke(); }
    public string IncludeLabel => IsExisting ? "Replace" : "Add";

    [ObservableProperty] private ColumnChoice? _selectedColumn;
    partial void OnSelectedColumnChanged(ColumnChoice? value) => Changed?.Invoke();

    /// <summary>Set when another song in the file is linked to the same column.</summary>
    [ObservableProperty] private string _warning = "";

    public event Action? Changed;

    [RelayCommand] private void Use() => Include = true;
    [RelayCommand] private void Skip() => Include = false;
}
