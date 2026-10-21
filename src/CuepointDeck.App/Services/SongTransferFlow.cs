using System.IO;
using System.Windows;
using CuepointDeck.App.ViewModels;
using CuepointDeck.App.Views;
using CuepointDeck.Core;
using CuepointDeck.Core.Library;
using CuepointDeck.Core.Logging;
using CuepointDeck.Core.Resolume;

namespace CuepointDeck.App.Services;

/// <summary>
/// Moving songs between campuses: export picked songs to one .cuedeck file, and import one, linking each song to a
/// column on this campus's song layer. Both start from Settings → Transfer or from Edit mode's menus.
/// </summary>
public static class SongTransferFlow
{
    public const string FileFilter = "Cuepoint Deck songs (*.cuedeck)|*.cuedeck|All files|*.*";

    /// <summary>Where the last export this session went, for "Show in Explorer".</summary>
    public static string? LastExportPath { get; private set; }
    public static event Action? LastExportChanged;

    // ------------------------------------------------------------------ export

    public static void Export(AppServices services, ShellViewModel shell, IEnumerable<string>? preselect = null, Window? owner = null)
    {
        var songs = services.Library.Songs.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (songs.Count == 0)
        {
            Alert.Inform("There are no songs to export", "Make songs in Edit mode first. They appear here once they're saved.");
            return;
        }
        var picked = ExportSongsSheet.Pick(songs, preselect, owner);
        if (picked is null || picked.Count == 0) return;

        var name = picked.Count == 1 ? SafeFileName(picked[0].Title) : $"Cuepoint Deck songs {DateTime.Now:yyyy-MM-dd}";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = picked.Count == 1 ? "Export Song" : $"Export {picked.Count} Songs",
            Filter = FileFilter,
            DefaultExt = SongTransfer.Extension,
            AddExtension = true,
            FileName = name + SongTransfer.Extension,
            InitialDirectory = LastExportPath is { } last ? Path.GetDirectoryName(last) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(owner ?? Sheet.FindOwner()) != true) return;

        var comp = services.Connection.Composition;
        var file = SongTransfer.Create(picked, s =>
        {
            var clip = services.Matches.For(s.Id)?.Clip;
            return clip is null ? null : comp?.Column(clip.Column)?.Name;
        });
        try
        {
            File.WriteAllText(dlg.FileName, SongTransfer.Serialize(file));
        }
        catch (Exception ex)
        {
            Log.Error($"Exporting songs to {dlg.FileName}", ex);
            Alert.Inform("The songs couldn't be exported", ex.Message);
            return;
        }
        LastExportPath = dlg.FileName;
        LastExportChanged?.Invoke();
        var what = picked.Count == 1 ? $"“{picked[0].Title}”" : $"{picked.Count} songs";
        Log.Info($"Exported {picked.Count} song(s) to {dlg.FileName}");
        shell.ShowToast($"Exported {what} to {Path.GetFileName(dlg.FileName)}");
    }

    public static void ShowLastExport()
    {
        if (LastExportPath is not { } path || !File.Exists(path)) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    private static string SafeFileName(string title)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(title.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Song" : clean;
    }

    // ------------------------------------------------------------------ import

    public static async Task ImportAsync(AppServices services, ShellViewModel shell, Window? owner = null)
    {
        // A song being edited might be replaced: settle its changes first.
        if (shell.Edit is { } edit && !edit.CanLeave()) return;

        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import Songs", Filter = FileFilter };
        if (dlg.ShowDialog(owner ?? Sheet.FindOwner()) != true) return;

        TransferFile file;
        try { file = SongTransfer.Parse(await File.ReadAllTextAsync(dlg.FileName)); }
        catch (FormatException ex) { Alert.Inform("This file can't be imported", ex.Message); return; }
        catch (Exception ex)
        {
            Log.Error($"Reading {dlg.FileName}", ex);
            Alert.Inform("This file can't be imported", ex.Message);
            return;
        }
        if (file.Songs.Count == 0) { Alert.Inform("This file has no songs in it", "Export it again with at least one song picked."); return; }

        var comp = services.Connection.Composition;
        var connected = services.Connection.State == ConnectionState.Connected && comp is not null;
        var clips = connected ? SongLayerClips(comp!, services.Settings.SongLayer) : new List<ClipInfo>();
        Func<int, string> columnName = c => comp?.Column(c)?.Name ?? "";
        var items = BuildItems(file, services.Library.Songs, s => services.Matches.For(s.Id)?.Clip, clips, columnName);

        var chosen = ImportSongsSheet.Ask(file, items, connected, services.Settings.SongLayer, owner);
        if (chosen is null) return;
        var (added, replaced, failed, imported) = Apply(services.Library, chosen);

        var parts = new List<string>();
        if (added > 0) parts.Add($"added {added}");
        if (replaced > 0) parts.Add($"replaced {replaced}");
        var summary = parts.Count == 0 ? "Nothing was imported" : "Imported: " + string.Join(", ", parts);
        Log.Info($"Import from {dlg.FileName}: {added} added, {replaced} replaced, {failed} failed, {chosen.Count(i => !i.Include)} skipped");
        if (failed > 0) Alert.Inform($"{failed} song{(failed == 1 ? "" : "s")} couldn't be saved", "The library folder may be unreachable or read-only. The log has the details.");
        shell.Edit?.ReloadSongs(imported);
        if (imported.Count == 0) { shell.ShowToast(summary); return; }

        // Thumbnails come from this campus's own video, so they're rendered here, in the background.
        shell.ShowToast($"{summary}. Rendering thumbnails…");
        var result = await ThumbnailRenderer.RenderAsync(services, imported);
        shell.Edit?.ReloadSongs(imported);
        if (result.FfmpegMissing) shell.ShowToast($"{summary}. Thumbnails are placeholders until ffmpeg is set up in Settings → Media.", warn: true);
        else if (result.SongsWithoutMedia > 0)
            shell.ShowToast($"{summary}. {result.SongsWithoutMedia} song{(result.SongsWithoutMedia == 1 ? "'s video isn't" : "s' videos aren't")} readable on this PC, so they keep colour placeholders.", warn: true);
        else shell.ShowToast($"{summary}. Thumbnails are ready.");
    }

    /// <summary>Clips a song can be linked to: the song layer's clips that have a media file.</summary>
    public static List<ClipInfo> SongLayerClips(Composition comp, int songLayer)
        => comp.Clips.Where(c => c.Layer == songLayer && !c.IsEmpty && !string.IsNullOrEmpty(c.FilePath)).OrderBy(c => c.Column).ToList();

    public static string ColumnLabel(ClipInfo clip, Func<int, string> columnName)
    {
        var name = columnName(clip.Column);
        return $"C{clip.Column}  ·  {(name.Length > 0 ? name : clip.Name)}";
    }

    /// <summary>One row per song in the file, with the song it would replace and a suggested column.</summary>
    public static List<ImportSongItem> BuildItems(TransferFile file, IReadOnlyList<Song> library, Func<Song, ClipInfo?> currentClip,
                                                  IReadOnlyList<ClipInfo> clips, Func<int, string> columnName)
    {
        var items = new List<ImportSongItem>();
        var claimed = new HashSet<string>();
        foreach (var incoming in file.Songs)
        {
            var existing = SongTransfer.FindExisting(incoming, library);
            // Two songs in one file can't both replace the same song here; the second comes in as new.
            if (existing is not null && !claimed.Add(existing.Id)) existing = null;
            var existingClip = existing is null ? null : currentClip(existing);

            var choices = new System.Collections.ObjectModel.ObservableCollection<ColumnChoice>();
            var keeps = existing is not null && (existingClip is not null || existing.Clip.FilePath.Length > 0);
            var none = new ColumnChoice { Label = keeps ? "Keep current link" : "Not linked", KeepsLink = keeps };
            choices.Add(none);
            foreach (var c in clips) choices.Add(new ColumnChoice { Clip = c, Label = ColumnLabel(c, columnName) });

            var suggested = SongTransfer.SuggestColumn(incoming, existing, existingClip, clips, columnName);
            var item = new ImportSongItem { Incoming = incoming, Existing = existing, Columns = choices };
            // Keeping a song on the column it already plays from is the same as leaving its link alone.
            item.SelectedColumn = suggested is null || (existingClip is not null && suggested.ClipId == existingClip.ClipId)
                ? (existingClip is not null ? choices.FirstOrDefault(c => c.Clip?.ClipId == existingClip.ClipId) ?? none : none)
                : choices.FirstOrDefault(c => c.Clip?.ClipId == suggested.ClipId) ?? none;
            items.Add(item);
        }
        return items;
    }

    /// <summary>Marks rows linked to a column another row also uses.</summary>
    public static void UpdateWarnings(IEnumerable<ImportSongItem> items)
    {
        var list = items.ToList();
        var counts = list.Where(i => i.Include && i.SelectedColumn?.Clip is not null)
                         .GroupBy(i => i.SelectedColumn!.Clip!.ClipId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var i in list)
        {
            var clip = i.SelectedColumn?.Clip;
            i.Warning = i.Include && clip is not null && counts.GetValueOrDefault(clip.ClipId) > 1
                ? $"Another song here is also linked to column {clip.Column}"
                : "";
        }
    }

    private static (int Added, int Replaced, int Failed, List<string> Imported) Apply(SongLibrary lib, IEnumerable<ImportSongItem> items)
    {
        int added = 0, replaced = 0, failed = 0;
        var ids = new List<string>();
        foreach (var item in items.Where(i => i.Include))
        {
            var song = SongTransfer.Apply(item.Incoming, item.Existing, item.SelectedColumn?.Clip);
            var r = lib.SaveSong(song, overwrite: true);
            if (!r.Ok) { failed++; Log.Warn($"Import: \"{song.Title}\" not saved: {r.Error}"); continue; }
            if (item.Existing is null) added++; else replaced++;
            ids.Add(song.Id);
        }
        return (added, replaced, failed, ids);
    }
}
