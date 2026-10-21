<img src="docs/icon.png" width="96" align="right" alt="Cuepoint Deck icon">

# Cuepoint Deck

Cuepoint Deck is a Windows app for Nova Church's visuals operator. It fires named sections of a song
(Verse 1, Chorus, Bridge…) inside a Resolume Arena clip by moving the clip's playhead, so a song can have
as many segments as it needs instead of Resolume's six cue points. Each segment is a card with a thumbnail,
a lyric note and a number key.

Resolume stays in charge of playback. If Cuepoint Deck crashes or loses its connection, the clip keeps
playing and the operator falls back to Resolume's own controls. Nothing in the app can stop or pause output.

The build brief is in `docs/cuepoint-deck-brief.md`. What Arena actually does on the wire, with measured
numbers, is in `docs/RESOLUME_NOTES.md`.

## Status

All seven stages are built and pass their tests on a development PC with Arena 7.24.1. What is still owed
on the real machines is listed under **What needs testing on the show PC** at the end.

| Stage | State |
|---|---|
| 1. Resolume spike | Done. Console probe + findings. Show PC and VLAN 131 runs still to do. |
| 2. Core | Done. Settings, logging, reconnecting connection, library with safe writes, clip matching. |
| 3. Show mode with cut | Done. Cards, whole-clip progress bar with ticks, cut by click and keys, seek verification, Follow role. |
| 4. Queue | Done. One-slot queue, countdown, Esc, Shift swap, playhead estimation, per-host latency offset. |
| 5. Edit mode | Done. New song from composition or file, Mark at playhead, filmstrip scrubber, thumbnails via ffmpeg. |
| 6. Setlists and song launch | Done. Setlist manager, strip, Left/Right/Enter, flash-free launch order. |
| 7. Hardening | 10-minute soak on the dev PC with the clip playing: CPU about 0.1% of a 16-core machine, working set settled at 184 MB after 4 minutes and stayed flat, handles and threads flat. README, self-contained publish. The 3-hour show-PC soak is still to do. |

Since 0.4.0 the interface follows Apple's Human Interface Guidelines; [docs/DESIGN.md](docs/DESIGN.md) explains how.

Screenshots from the development PC: [Show mode](docs/screenshots/show-mode.png),
[a card's menu](docs/screenshots/show-mode-menu.png), [Edit mode](docs/screenshots/edit-mode.png),
[a setlist](docs/screenshots/edit-setlist.png), [Settings](docs/screenshots/settings.png),
[an alert](docs/screenshots/alert.png).

## Setup

### On the Resolume PC

1. In Arena: **Preferences > Webserver**, tick **Enable Webserver & REST API**. Leave the port at 8080.
   Set the listen address to `0.0.0.0` only if Cuepoint Deck will run on another PC.
2. Song clips should be on one layer (the **Song layer** setting, default 1).
3. Recommended: in each song clip's transport settings set the retrigger option to **Continue**
   (Restart / Continue / Relative; the API calls it `playmodeaway`) and save the composition. With Continue,
   launching a song at a segment is a clean seek-then-connect. With the default Restart, Cuepoint Deck uses the
   clip's in-point instead, which also works but touches a clip setting for half a second.

### Cuepoint Deck

No installer. Copy `CuepointDeck.exe` anywhere and run it. No admin rights, no .NET install, no login.

First start: it connects to `127.0.0.1:8080` and creates `Documents\CuepointDeck Library`.
Open **Settings** (the gear, or Ctrl+,) to change these. Settings is an ordinary window with panes, and every change
applies as you make it: switches and pop-ups at once, text fields when you press Return or move on. There is no Save.

| Setting | Notes |
|---|---|
| Resolume host / port | Use the IP on VLAN 131 when running remotely. Prefer `127.0.0.1` over `localhost` locally. |
| Role | **Controller** fires segments. **Follow** shows everything but cannot fire. Use Follow for a producer or FOH screen. |
| Library path | A local folder, or a UNC share both PCs can see. |
| Song layer | The layer whose clips are the songs (default 4 here). Copies of the same file on other layers of the column are expected and ignored; two columns on the song layer with the same file is flagged. |
| Latency offset | How early a queued jump is sent. Stored per host. Start at 40 ms locally; raise it remotely until queued jumps land on the boundary. |
| Default trigger | What a plain click or number key does: Queue (default) or Cut. Shift does the other one. |
| Launch songs that aren't live | Off: firing a segment of a song that isn't live is refused. On: it brings that song up at the segment. A click on a setlist song always brings up its column, whatever this is set to. |
| Path mappings | `D:\Media => \\CITY-VISUALS\Media`, one per line. For editing on a PC that sees the media at a different path. |
| ffmpeg path | Only needed for Edit mode thumbnails and the preview scrubber. Download a Windows build from ffmpeg.org, unzip it, point this at `ffmpeg.exe`. |
| Card size | 1.0 is about 240 px wide at 100% scaling. Ctrl + and Ctrl − change it from Show mode too. |
| Ask before quitting | On by default. The quit question's "Don't ask again" turns it off. |

Keep on Top is the pin in the toolbar rather than a setting, since it changes from moment to moment.

Settings live in `%AppData%\CuepointDeck\settings.json`. Logs go to `%AppData%\CuepointDeck\logs\`, one file per
day, kept 14 days. **Settings → About → Show Log** shows the last 400 lines live.

### Firewall, when Cuepoint Deck runs on another PC

Resolume's webserver has no authentication. Anyone who can reach port 8080 can control the composition.
On the Resolume PC add a Windows Firewall rule that allows inbound TCP 8080 **only from the operator PC's IP**,
and keep both machines on VLAN 131, away from the staff VLAN. In an elevated PowerShell on the Resolume PC:

```powershell
New-NetFirewallRule -DisplayName "Resolume webserver (Cuepoint Deck)" -Direction Inbound -Protocol TCP -LocalPort 8080 -RemoteAddress 10.131.0.50 -Action Allow
```

Replace the address with the operator PC's IP.

### One Controller at a time

Nothing detects or blocks two Controllers on the same Resolume. Run one Controller; any extra instances
must be set to the Follow role.

## Using it

The interface follows Apple's Human Interface Guidelines, translated to Windows; [docs/DESIGN.md](docs/DESIGN.md)
lists what that means for each part.

### The toolbar

One row across the top, the same in both modes:

- **Connection status** at the leading edge: a green, amber or red dot with "Resolume", "Connecting…" or "Offline".
  Click it for the address, Resolume's version, what is live, the last error, and a button to the connection settings.
- **The setlist** (Show mode): a pop-up of saved setlists and the songs in order. A click on a song brings up its
  column in Resolume; right-click for Edit Song or Edit Setlist. The live song has a green dot. On narrow windows the
  setlist moves to a row of its own. In Edit mode this space just says nothing there is sent to Resolume.
- **⚠ with a number** appears only when something needs attention (an unreadable library file, a song in two columns
  on the song layer, ffmpeg missing in Edit mode). Click it for the list, each with the action that fixes it.
- **Follow · Read Only** appears only on a PC set to Follow.
- **Pin**: keeps the window above Resolume when you click away. Lit when on; remembered.
- **Show | Edit**: the two modes of the one window. E and Esc switch from the keyboard.
- **Keyboard** opens the list of shortcuts (also F1). **Gear** opens Settings (also Ctrl+,).

Short confirmations ("Jumped to Chorus", "Deleted Bridge. Press Ctrl+Z to bring it back.") appear briefly at the
bottom of the window and never block a click.

### Show mode

- When a song clip is connected in Resolume, its song appears with the whole-clip progress bar (segment boundaries
  as ticks) and the segment cards. The live segment is lit in its colour; the one that would play next says "Next".
- With nothing to show, the middle of the window says why (Resolume offline, nothing live, a clip that isn't in the
  library) and what to do. An unknown clip gets an **Add to Library…** button.
- A click or number key fires a card with the default trigger; Shift does the other. **Jump** goes now. **Queue**
  waits for the current segment to end, then jumps; a queued card pulses with a countdown. One segment can be queued;
  queuing another replaces it. If the queued segment is the natural next one, nothing is sent. A paused clip holds
  the queue.
- Right-click a card for **Jump Now**, **Jump at End of Current Segment** (each showing its key), **Loop This
  Segment** and **Edit Segment**, which opens Edit mode on that segment.
- **Loop** (the loop button on a card, L for the live segment, or the menu) repeats a segment: each time the playhead
  reaches its end it seeks back to the segment's start, until you turn it off, press Esc, or the song changes. The card
  shows "LOOP · 3.2 s" counting down to each repeat. A Queue you set takes precedence at the boundary.
- If the song's clip isn't live: with "Launch songs that aren't live" off the card flashes red and a capsule in the
  header says so; with it on, the song is brought up at that segment.
- After every seek the app checks the playhead landed within 250 ms of the target within 500 ms and warns if not.
  It never retries on its own.
- Losing the connection clears any queue and dims the cards; the app reconnects every 2 s on its own.
- Ctrl + and Ctrl − make the cards bigger or smaller; Ctrl+0 resets.
- Quitting asks first ("Quit Cuepoint Deck?", with Cancel as the default so a stray Return can't close the deck
  mid-service). "Don't ask again" turns it off; Settings → General turns it back on. Resolume keeps playing regardless.

Keys (only while the Cuepoint Deck window has focus; nothing is global because Resolume uses the same keys):

| Key | Action |
|---|---|
| 1–9, 0 | Fire segment 1–10 with the default trigger |
| Shift + 1–9, 0 | Fire with the other trigger |
| L | Loop the live segment on / off |
| Esc | Clear the queue and the loop |
| Left / Right | Choose the previous / next song in the setlist |
| Return | Bring up the chosen song (triggers its column in Resolume); a click on the song does the same |
| Ctrl + / − / 0 | Bigger / smaller / normal cards |
| E | Edit the live song |
| F1 | Keyboard shortcuts |
| Ctrl + , | Settings |

### Edit mode

Edit mode never sends anything to Resolume, so it is safe during a service. It is laid out like a library app: a
sidebar of **Songs** (with search) and **Setlists**, and the editor for whichever you select.

It always fits the window; nothing is reached by scrolling the page. The sidebar button at the toolbar's leading edge
hides the sidebar; below about 1000 px wide the sidebar tucks away on its own and that button slides it over the
editor (choosing a song or setlist, a click beside it, or Esc puts it away). The editor and the preview share the width,
the preview picture gives up height before its controls do, and on short windows the segment inspector keeps a usable
list above it and scrolls inside itself. When the segments panel is narrow, Add at Preview and Auto step aside; the
list's **+** menu always has every way to add segments.

- **+** at the bottom of the sidebar: **New Song from Resolume…** (a sheet listing the columns on the song layer, named
  after the column; columns already in the library are marked and open the existing song instead of duplicating it),
  **New Song from File…**, and **New Setlist…**. If Resolume isn't connected, New Song from Resolume says so and
  offers the file route instead of an empty list.
- Right-click a song to delete it, or a setlist for **Use in Show Mode**, **Rename…**, **Duplicate…** and **Delete
  Setlist…**. Deleting always asks, names what goes, and keeps a backup copy.
- **The song**: the title and artist edit in place. **Save** is the one prominent button and lights up only when there
  is something to save (Ctrl+S). The ⋯ menu has Render Missing Thumbnails, Show Library Folder and Delete Song….
  Leaving a song with unsaved changes asks **Save**, **Don't Save** or **Cancel**.
- **Add at Playhead** (M): with the clip playing in Resolume, press M at each section. **Add at Preview** (Shift+M)
  adds at the preview position. **Auto** has **Suggest Segments…** and **Read Lyric Notes from the Video**.
- **Suggest Segments** analyses the song on this PC (no internet, no outside service) and adds draft segments. It
  decodes the audio with ffmpeg, finds where the harmony and timbre change, snaps those points to hard video cuts, and
  names the parts by repetition: the most repeated, loudest block is the chorus, the earliest repeated block the
  verses, a repeated block that always precedes a chorus the pre-chorus, one-off blocks late in the song the bridge,
  quiet short ends the intro and outro. If the song already has segments it asks whether to **Replace** them or **Add
  as Drafts**. Check each draft with the exact-frame preview, then **Keep Drafts** or **Discard Drafts**. Mark at
  playhead during rehearsal is still the most accurate method for songs with unusual structure.
- **Read Lyric Notes** fills empty lyric notes with the on-screen text a couple of seconds after each segment starts,
  using the text recognition built into Windows 10/11 (offline). Treat the notes as drafts and fix the odd word.
- **Segments list**: right-click a segment for Show in Preview, Set Start to Resolume's Playhead, Set Start to the
  Preview Position and Delete Segment. **+** and **−** under the list add and delete. Delete removes the selected
  segment without asking; **Ctrl+Z** brings it back, and nothing is written until Save.
- **The selected segment**: **Name** is a combo box: type anything (Bridge 2, Big Chorus) or pick a usual name.
  **Starts** takes `hh:mm:ss:ff` at the clip's frame rate (Return applies), with − and + for one frame (Shift: five)
  and **Set Start** for Resolume's playhead, the preview position or Show Start in Preview. **Ends Early** gives the
  segment its own end; the stretch up to the next segment is then a gap that belongs to nothing: no card is lit
  there, a queued jump fires at the end, and the progress bar shows the gap hatched. Without it the segment runs
  until the next one starts. **Lyric** and **Colour** (the current colour is ringed) finish it off.
- **Preview**: ffmpeg builds a small preview video once (cached in the library). Space plays and pauses with sound,
  the slider and the 2-second frames below jump, ← → step one frame and show it at once, Shift+← → one second. Until
  the preview video exists the same controls use stills.
- Thumbnails, the preview box and the frames always keep the clip's own shape: a 16:9 song shows 16:9, a 2688x512
  stage canvas shows as a full-width strip. Nothing is cropped or letterboxed.
- Times everywhere read `hh:mm:ss:ff`, the way Resolume shows them. Time fields also accept `mm:ss:ff`, `mm:ss.fff`
  or plain seconds.
- **Save** writes the song and then renders a 480 px thumbnail per segment at its exact start. If ffmpeg or the media
  file isn't reachable on this PC, the song still saves and the cards show a coloured placeholder.
- If the song file changed on another PC since it was opened here, Save asks: **Save Mine**, **Use Their Version** or
  **Cancel**.

### Setlists

Setlists are edited in Edit mode: select one in the sidebar. The **Running Order** is on the left (drag to reorder,
or use the arrows under it; Delete removes the selected song from the setlist, the song itself stays in the library).
**Add Songs** on the right lists **Resolume's columns on the song layer**, with search: Resolume holds the whole
catalogue, so a service is picked from it column by column. Double-click a column or press **Add to Setlist**. A
column whose clip already has a song adds that song; one that doesn't creates a stub song (title from the column, no
segments yet) to build later. Changes save as you make them. **Use in Show Mode** makes it the setlist in Show mode's
toolbar; the one on show is marked **On Show**.

Show mode also works without any setlist; it just follows whatever clip is connected.

## Updates

New versions are published as [GitHub releases](https://github.com/DavidPujals/cuepoint-deck/releases). Inside the
app, **Settings → About → Check for Updates** compares the running version against the latest release; if
there is a newer one it downloads it and swaps the exe in place. Click **Restart Now** to finish. Resolume keeps playing
while the app restarts, and your settings and library are untouched. Do it between services, not during one.

### Cutting a release (maintainers)

1. Bump `<Version>` in `src/CuepointDeck.App/CuepointDeck.App.csproj` (e.g. `0.3.0`).
2. Publish, then create the release with the exe under the asset name `CuepointDeck.exe`:

   ```bash
   dotnet publish src/CuepointDeck.App/CuepointDeck.App.csproj -c Release -o publish/CuepointDeck
   ```

   ```bash
   gh release create v0.3.0 publish/CuepointDeck/CuepointDeck.exe --title "v0.3.0" --notes "What changed"
   ```

   The tag (`v0.3.0`) must match the csproj version, and each release should carry exactly one `.exe` asset.

## Library layout

```
Library/
  songs/<songId>.json           one file per song (+ .bak of the previous version)
  thumbs/<songId>/<segId>.jpg   card thumbnails
  thumbs/<songId>/<hash>.filmstrip/   scrubber frames, safe to delete
  setlists/<name>.json
```

Songs match clips by the source file path, then by path with the mappings applied, then by file name. A song
whose file is in more than one clip prefers the song layer and shows a warning. A song whose file isn't in the
composition shows as unavailable. A broken JSON file is skipped with a warning and never stops the app.

## Building

The app icon is `src/CuepointDeck.App/Assets/cuepointdeck.svg`. The `.ico` (16 to 256 px) and the PNGs next to it
and in `docs/` are rendered from it; re-render them if the SVG changes.

```bash
dotnet build CuepointDeck.sln -c Release
```

```bash
dotnet test tests/CuepointDeck.Core.Tests
```

Two tests are opt-in: `CUEPOINTDECK_ARENA_TESTS=1` kills and relaunches a local Arena to prove the reconnect
(never on the show PC during a service); `CUEPOINTDECK_FFMPEG` + `CUEPOINTDECK_TEST_VIDEO` exercise ffmpeg.

Self-contained single-file publish (copy `CuepointDeck.exe` to any Windows 10/11 x64 PC):

```bash
dotnet publish src/CuepointDeck.App/CuepointDeck.App.csproj -c Release -o publish/CuepointDeck
```

Layout:

```
src/CuepointDeck.Core/     settings, logging, Resolume connection, library, matching, trigger engine (ISegmentController)
src/CuepointDeck.App/      WPF: Show mode, Edit mode, setlists, settings, log
tests/                    xunit, with recorded Arena messages as fixtures
spike/                    Stage 1 test window and console probe
samples/library/          a tiny library to try the app with
docs/                     brief, Resolume notes, probe reports, screenshots
```

## What needs testing on the show PC

Everything below was only checked on a development PC with Arena on the same machine.

1. **Stage 1 probe, twice**: on the show PC (`--host 127.0.0.1`) and from the operator PC over VLAN 131
   (`--host <show PC IP>`). Save the reports in `docs/probe-reports/`. Command in `docs/RESOLUME_NOTES.md`.
2. **Eyes on the projector** for the launch orders: launch a song at a mid-song segment with "Launch songs
   that aren't live" on, once with the clip's retrigger set to Continue and once with Restart. There should be no
   flash of the clip's first frame.
3. **Setlist click**: click a song in the top bar and confirm the whole column comes up in Resolume (all layers), and
   the cards switch to it.
4. **Loop**: loop a chorus and watch several repeats land on the beat; then Queue a bridge during the loop and
   confirm the queue wins at the boundary and the loop stays idle afterwards.
5. **Queue timing**: queue a chorus during a verse, locally and remotely, and tune the latency offset per host
   until the jump lands within a frame or two of the boundary.
6. **Resilience**: kill and restart Resolume with the app connected; unplug the operator PC's network for
   30 s. Both should recover without touching the app.
7. **Library on a share** from two PCs: edit the same song on both and confirm the overwrite/reload prompt.
8. **Edit mode remote**: edit on the operator PC with a path mapping (thumbnails render) and without one
   (clear message, placeholders, song still saves).
9. **Mock services**: five songs from a setlist with launch on, then with launch off.
10. **Three-hour soak** on the show PC with Resolume playing: memory, CPU and GPU use should stay flat.
   The Log window and Task Manager are enough.
11. **Sheets and menus on the real screen**: alerts, the name prompt and the column picker should drop from just
   under the toolbar with a shadow and rounded corners and no title bar; context menus and pop-ups should have rounded
   corners and a shadow. (Screen captures on the development PC can't show window chrome.)
