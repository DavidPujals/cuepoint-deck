# Segment Deck

Segment Deck is a Windows app for Nova Church's visuals operator. It fires named sections of a song
(Verse 1, Chorus, Bridge…) inside a Resolume Arena clip by moving the clip's playhead, so a song can have
as many segments as it needs instead of Resolume's six cue points. Each segment is a card with a thumbnail,
a lyric note and a number key.

Resolume stays in charge of playback. If Segment Deck crashes or loses its connection, the clip keeps
playing and the operator falls back to Resolume's own controls. Nothing in the app can stop or pause output.

The build brief is in `docs/segment-deck-brief.md`. What Arena actually does on the wire, with measured
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

Screenshots from the development PC: [Show mode](docs/screenshots/show-mode.png),
[Show mode with a queued segment](docs/screenshots/show-mode-queued.png), [Edit mode](docs/screenshots/edit-mode.png).

## Setup

### On the Resolume PC

1. In Arena: **Preferences > Webserver**, tick **Enable Webserver & REST API**. Leave the port at 8080.
   Set the listen address to `0.0.0.0` only if Segment Deck will run on another PC.
2. Song clips should be on one layer (the **Song layer** setting, default 1).
3. Recommended: in each song clip's transport settings set the retrigger option to **Continue**
   (Restart / Continue / Relative; the API calls it `playmodeaway`) and save the composition. With Continue,
   launching a song at a segment is a clean seek-then-connect. With the default Restart, Segment Deck uses the
   clip's in-point instead, which also works but touches a clip setting for half a second.

### Segment Deck

No installer. Copy the `SegmentDeck` publish folder anywhere and run `SegmentDeck.exe`. No admin rights,
no .NET install, no login.

First start: it connects to `127.0.0.1:8080` and creates `Documents\SegmentDeck Library`.
Open **Settings** to change:

| Setting | Notes |
|---|---|
| Resolume host / port | Use the IP on VLAN 131 when running remotely. Prefer `127.0.0.1` over `localhost` locally. |
| Role | **Controller** fires segments. **Follow** shows everything but cannot fire. Use Follow for a producer or FOH screen. |
| Library path | A local folder, or a UNC share both PCs can see. |
| Song layer | Breaks ties when a song's file is in more than one clip, and is where songs are launched. |
| Latency offset | How early a queued jump is sent. Stored per host. Start at 40 ms locally; raise it remotely until queued jumps land on the boundary. |
| Default trigger | What a plain click or number key does: Queue (default) or Cut. Shift does the other one. |
| Launch songs from setlist | Off: you start songs in Resolume and Segment Deck follows. On: Enter / the Launch button connects the selected song, and firing a segment of a song that isn't live launches it there. |
| Path mappings | `D:\Media => \\CITY-VISUALS\Media`, one per line. For editing on a PC that sees the media at a different path. |
| ffmpeg path | Only needed for Edit mode thumbnails and the preview scrubber. Download a Windows build from ffmpeg.org, unzip it, point this at `ffmpeg.exe`. |
| Card size | 1.0 is about 240 px wide at 100% scaling. |
| Always on top | |

Settings live in `%AppData%\SegmentDeck\settings.json`. Logs go to `%AppData%\SegmentDeck\logs\`, one file per
day, kept 14 days. The **Log** button shows the last 400 lines.

### Firewall, when Segment Deck runs on another PC

Resolume's webserver has no authentication. Anyone who can reach port 8080 can control the composition.
On the Resolume PC add a Windows Firewall rule that allows inbound TCP 8080 **only from the operator PC's IP**,
and keep both machines on VLAN 131, away from the staff VLAN. In an elevated PowerShell on the Resolume PC:

```powershell
New-NetFirewallRule -DisplayName "Resolume webserver (Segment Deck)" -Direction Inbound -Protocol TCP -LocalPort 8080 -RemoteAddress 10.131.0.50 -Action Allow
```

Replace the address with the operator PC's IP.

### One Controller at a time

Nothing detects or blocks two Controllers on the same Resolume. Run one Controller; any extra instances
must be set to the Follow role.

## Using it

### Show mode

- The top bar shows the connection light (green connected, amber reconnecting, red disconnected), the host,
  warnings, and the role badge.
- When a song clip is connected in Resolume, its song appears automatically with the whole-clip progress bar
  (segment boundaries as ticks) and the segment cards. The live segment is lit in its colour; the one that
  would play next has a small "next" marker.
- If the connected clip is not in the library, the header says "Unknown clip" with an **Add to library** button.
- **Cut** jumps now. **Queue** waits for the current segment to end, then jumps. A queued card pulses with a
  countdown. Only one segment can be queued; queuing another replaces it. If the queued segment is the natural
  next one, nothing is sent. A paused clip holds the queue.
- If the song's clip isn't live: with "Launch songs from setlist" off the card flashes red and the bar says
  "Clip not live"; with it on, the clip is launched at that segment.
- After every seek the app checks the playhead landed within 250 ms of the target within 500 ms and warns if not.
  It never retries on its own.
- Losing the connection clears any queue and greys the cards; the app reconnects every 2 s on its own.

Keys (only while the Segment Deck window has focus; nothing is global because Resolume uses the same keys):

| Key | Action |
|---|---|
| 1–9, 0 | Fire segment 1–10 with the default trigger |
| Shift + 1–9, 0 | Fire with the other trigger |
| Esc | Clear the queue |
| Left / Right | Select previous / next song in the setlist strip |
| Enter | Launch the selected song (only with "Launch songs from setlist" on) |
| E | Edit mode for the current song |

### Edit mode

Edit mode never sends anything to Resolume, so it is safe during a service.

- **New song from composition…** lists the clips with a media file; **New song from a file…** browses.
  The title defaults to the clip name; the duration comes from Resolume, or from ffprobe.
- **Mark at Resolume playhead (M)**: with the clip playing in Resolume, press M at each section.
- **Scrubber**: a filmstrip of one frame every 2 s, built once by ffmpeg at low priority and cached in the
  library. Drag the playhead, ←/→ steps a frame, Shift+←/→ a second, M marks at the scrubber when the clip
  isn't live (Shift+M always).
- Each segment: name (quick picks), start time (`mm:ss.fff`, nudge ±1 frame and ±100 ms), colour, lyric note.
  Segments always sort by start time.
- **Save** writes the song and then renders a 480 px thumbnail per segment at its exact start. If ffmpeg or the
  media file isn't reachable on this PC, the song still saves and the cards show a coloured placeholder.
- If the song file changed on disk since it was loaded (edited from the other PC), Save asks whether to
  overwrite or reload.

### Setlists

**Setlists** (button in the top bar, or Manage… in the strip): create, rename, duplicate, delete; add songs
from the library, drag or use Up/Down to reorder, remove. **Use in Show mode** makes one active. Show mode
works without a setlist; it just follows whatever clip is connected.

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

```bash
dotnet build SegmentDeck.sln -c Release
```

```bash
dotnet test tests/SegmentDeck.Core.Tests
```

Two tests are opt-in: `SEGMENTDECK_ARENA_TESTS=1` kills and relaunches a local Arena to prove the reconnect
(never on the show PC during a service); `SEGMENTDECK_FFMPEG` + `SEGMENTDECK_TEST_VIDEO` exercise ffmpeg.

Self-contained publish (copy the folder to any Windows 10/11 x64 PC):

```bash
dotnet publish src/SegmentDeck.App/SegmentDeck.App.csproj -c Release -o publish/SegmentDeck
```

Layout:

```
src/SegmentDeck.Core/     settings, logging, Resolume connection, library, matching, trigger engine (ISegmentController)
src/SegmentDeck.App/      WPF: Show mode, Edit mode, setlists, settings, log
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
   from setlist" on, once with the clip's retrigger set to Continue and once with Restart. There should be no
   flash of the clip's first frame.
3. **Queue timing**: queue a chorus during a verse, locally and remotely, and tune the latency offset per host
   until the jump lands within a frame or two of the boundary.
4. **Resilience**: kill and restart Resolume with the app connected; unplug the operator PC's network for
   30 s. Both should recover without touching the app.
5. **Library on a share** from two PCs: edit the same song on both and confirm the overwrite/reload prompt.
6. **Edit mode remote**: edit on the operator PC with a path mapping (thumbnails render) and without one
   (clear message, placeholders, song still saves).
7. **Mock services**: five songs from a setlist with launch on, then with launch off.
8. **Three-hour soak** on the show PC with Resolume playing: memory, CPU and GPU use should stay flat.
   The Log window and Task Manager are enough.
