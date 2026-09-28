# Segment Deck: build brief

Segment Deck is a Windows app for Nova Church's visuals operator. It triggers named sections of songs (Verse 1, Chorus, Bridge and so on) inside Resolume Arena clips by seeking the clip's playhead. Resolume only gives each clip six cue points, and most of our songs need more than that. Each song in the app can have as many segments as it needs, and every segment shows a thumbnail of its first frame plus a short lyric note so the operator knows what they're about to fire.

"Segment Deck" is a working name. Rename it if something better comes up.

Build it in stages. Stop at the end of each stage so it can be tested on a real Resolume machine before moving on.

## Context

- Each song in Resolume is one full-length clip. Segments are start times inside that clip.
- The app usually runs on the same PC as Resolume, but it must also run on a different PC on the production network (VLAN 131) and control Resolume remotely.
- Resolume is the playback engine and stays in charge. If Segment Deck crashes, hangs or loses its connection, the clip keeps playing and the operator falls back to Resolume's own controls. Nothing in this app should be able to stop or pause output.
- The show PC is running Resolume, which is heavy on the GPU and CPU. Segment Deck has to stay light while a service is running.
- The look should match the speaker timer app we built earlier (C#/WPF, dark, high contrast, opens and works with no login or setup wizard). If that repo is available, reuse its styles and window conventions.

## Stack

- C# on .NET 8, WPF, MVVM (CommunityToolkit.Mvvm is fine).
- Windows 10 and 11, 64-bit.
- `System.Net.WebSockets.ClientWebSocket` for the Resolume WebSocket, `HttpClient` for REST calls, `System.Text.Json` for everything on disk.
- ffmpeg for thumbnails. Don't bundle it in the first builds. Add a setting for the path to `ffmpeg.exe` and check it on startup.
- Publish as a self-contained single-folder build. No installer, no admin rights, no services.
- No cloud, no accounts, no telemetry.

## Resolume integration

Resolume Arena's webserver is enabled under Preferences > Webserver and listens on port 8080 by default. It exposes a REST API and a WebSocket API on the same port. Treat Resolume's own documentation for the installed Arena version as the source of truth for paths, message formats and value units. Where this brief names an endpoint or parameter path, it's a starting point to verify, and Stage 1 exists to do that verifying.

What the app needs from Resolume:

1. The composition: layers, clips, clip names, and each clip's source file path.
2. Which clip is currently connected (playing) on each layer, with updates when that changes.
3. The playhead position of the live clip, streamed often enough to detect segment boundaries.
4. The clip's duration and playback speed.
5. The ability to set a clip's transport position (the seek that fires a segment).
6. The ability to connect (launch) a clip.

The WebSocket is the main channel. It pushes the full composition on connect and then sends changes, and the app sends parameter sets and triggers back down the same socket. Use REST as a fallback for one-off reads if the WebSocket doesn't expose something cleanly. Keep OSC out of the build unless Stage 1 shows WebSocket seeks are too slow.

Read the position parameter's min and max from Resolume and convert everything to milliseconds inside the app. Don't assume the units.

### Matching clips to songs

Never store layer or column numbers as the identity of a song. Operators rearrange the deck. A song is matched to a clip by its source file path, with the file name as a fallback (so the same library works at North and City, where drive letters or folders may differ). Each time the app connects, or the composition changes, it rebuilds a lookup from song to current clip location.

If a song's file matches more than one clip in the composition, prefer the one on the layer set in settings ("Song layer"), and show a warning on that song's card.

If a song's file isn't in the composition, show the song as unavailable. Don't hide it.

## Data and storage

### Song library

The library is a folder. Its path is a setting, so it can live locally or on a network share that both PCs can see.

```
Library/
  songs/
    3f2c9a1e-....json
  thumbs/
    3f2c9a1e-.../
      7b1d....jpg
      7b1d....filmstrip/   (edit-mode preview frames, safe to delete)
  setlists/
    2026-10-04 North AM.json
```

Song file:

```json
{
  "schemaVersion": 1,
  "id": "3f2c9a1e-...",
  "title": "Song title",
  "artist": "",
  "clip": {
    "filePath": "D:\\Media\\Songs\\Song title.mov",
    "fileName": "Song title.mov",
    "clipName": "Song title"
  },
  "durationMs": 312000,
  "segments": [
    {
      "id": "7b1d...",
      "name": "Verse 1",
      "startMs": 0,
      "color": "#4A90D9",
      "lyric": "First line of the verse",
      "thumb": "thumbs/3f2c9a1e-.../7b1d....jpg"
    }
  ],
  "updatedAt": "2026-10-01T09:30:00Z"
}
```

A segment ends where the next one starts. The last segment runs to the end of the clip. Keep segments sorted by `startMs` on save.

Setlist file:

```json
{
  "schemaVersion": 1,
  "name": "2026-10-04 North AM",
  "songIds": ["3f2c9a1e-...", "a91e44d0-..."]
}
```

### Safe writes

The library may be on a share and edited from two PCs, so:

- Write to a temp file in the same folder, then rename over the original. Keep the previous version as `.bak`.
- Before saving, check whether the file on disk changed since it was loaded. If it did, ask the operator whether to overwrite or reload.
- Re-scan the library when the window regains focus and on a manual refresh button. Don't rely on `FileSystemWatcher` for network shares.
- A corrupt or unreadable song file is skipped with a warning in the status area. It must never stop the app from starting.

### Per-machine settings

Stored in `%AppData%\SegmentDeck\settings.json`, never in the library, because each PC has its own connection details.

| Setting | Default | Notes |
|---|---|---|
| Resolume host | `localhost` | Hostname or IP of the Resolume PC |
| Resolume port | `8080` | |
| Role | Controller | Controller or Follow (see below) |
| Library path | `%Documents%\SegmentDeck Library` | Local folder or UNC path |
| Song layer | 1 | Used to break ties and to launch songs |
| Latency offset (ms) | 40 | How early a queued jump fires. Stored per host |
| Launch songs from setlist | Off | See "Song launch" |
| Default trigger | Queue | What a plain click or number key does |
| Path mappings | none | Pairs like `D:\Media` to `\\CITY-VISUALS\Media` |
| ffmpeg path | empty | Full path to `ffmpeg.exe` |
| Always on top | Off | |

Logs go to `%AppData%\SegmentDeck\logs\`, rolling daily, kept for 14 days. Log connection changes, every trigger sent, every seek verification result and every error.

## Show mode

This is the view the operator uses during a service.

Layout, top to bottom:

- A status bar with the connection light (green connected, amber connecting, red disconnected), the host name, the role badge (CONTROLLER or FOLLOW), and a message area for warnings.
- The current song: title, a progress bar for the whole clip with segment boundaries drawn on it as ticks, and elapsed and remaining time.
- The segment cards in a wrapping grid. Each card has the thumbnail, the segment name, the lyric note, its number (1 to 9, then 0 for 10) and a thin progress bar showing how far through that segment the playhead is.
- A setlist strip (collapsible) showing the songs in the current setlist, with the current one highlighted.

The live segment's card is lit in its segment colour. The segment that will play next if nothing is triggered gets a subtle "next" marker.

When a clip is connected in Resolume, Show mode switches to that song automatically if it's in the library. If the connected clip isn't in the library, show "Unknown clip: <name>" and an Add to library button that opens Edit mode for it.

Cards need to be readable from a normal seated distance on a 1080p screen. Aim for cards around 240px wide at 100% scaling, with a size slider in settings.

### Triggering

Every card can be fired two ways:

- Cut: seek straight to the segment's start.
- Queue: wait until the current segment ends, then seek.

Default trigger decides which one a plain click or number key does. The other is on Shift. So with the default of Queue, click queues and Shift-click cuts. Right-click on a card always opens a small menu with both.

A queued card shows a pulsing outline and a countdown to when it'll fire. Queuing another card replaces the queue (only one segment can be queued). Esc clears it. If the queued segment is the one that would play next anyway, the app doesn't send anything at the boundary and just clears the queue once the clip gets there.

If the clip is paused, a queued segment waits. If the operator cuts while something is queued, the cut happens and the queue is cleared.

If the song's clip isn't connected when a segment is fired: when Launch songs from setlist is on, connect the clip and then seek (using the order Stage 1 proves works). When it's off, flash the card red and show "Clip not live" in the status bar. Don't send anything.

### Boundary timing

The app learns the playhead position through the WebSocket, which will have some update interval plus network delay when remote. To fire queued jumps on time:

- Keep a local estimate of the playhead, advanced by a high-resolution timer between updates using the clip's playback speed, and corrected every time a real position arrives.
- Fire the queued seek when the estimate reaches `segment end - latency offset`.
- Latency offset is a setting stored per host, so a local setup and a remote setup can each be tuned.

### Seek verification

After every seek, watch the next position updates. If the playhead isn't within 250 ms of the target within 500 ms, show a warning on the status bar and log it. Don't retry automatically. A second jump is worse than a missed one.

### Connection loss

- Retry every 2 seconds with no limit, quietly, with the light amber while retrying.
- Clear any queued segment the moment the connection drops. Never fire a queue late after reconnecting.
- On reconnect, resync the composition, the connected clip and the position, then return to normal.
- Cards stay visible while disconnected but can't be fired.

### Keyboard

Shortcuts only work while the Segment Deck window has focus. Don't register global hotkeys, because Resolume uses the same keys on the same PC.

| Key | Action |
|---|---|
| 1 to 9, 0 | Fire segment 1 to 10 using the default trigger |
| Shift + 1 to 9, 0 | Fire segment 1 to 10 using the other trigger |
| Esc | Clear queue |
| Left / Right | Select previous / next song in the setlist |
| Enter | Launch the selected song (only when Launch songs from setlist is on) |
| E | Switch to Edit mode for the current song |

There's no key that pauses or stops Resolume, on purpose.

### Song launch

When Launch songs from setlist is off (the default), the operator starts songs in Resolume and Segment Deck follows. Left and Right only move the selection in the setlist strip.

When it's on, Enter (or a Launch button on the setlist strip) connects the selected song's clip on the song layer. Segment Deck follows whatever's connected either way, so launching a clip from Resolume still works.

### Roles

Controller can fire segments and launch songs. Follow is read-only: it shows everything Show mode shows, but cards, keys and launch controls are disabled and the role badge says FOLLOW. Use Follow for a second instance on a producer's screen or at FOH.

Nothing detects or blocks two Controllers on the same Resolume. It's up to the team to run one. Put a note about this in the README.

## Edit mode

Edit mode never sends commands to Resolume. It only reads from it. That makes it safe to edit songs during a service.

Creating a song:

- Pick from a list of clips in the current composition (showing clip name, layer and file name), or browse to a media file directly.
- Title defaults to the clip name.
- Duration comes from Resolume if the clip is in the composition, otherwise from ffprobe.

Marking segments has two methods, both needed.

The first is Mark at Resolume playhead. During rehearsal, with the clip playing in Resolume, the operator presses M and a segment is added at the current position. This is the main method, because the operator can hear the band or the track.

The second is the preview scrubber. It's a filmstrip of frames pulled from the file with ffmpeg (one every 2 seconds, generated at low priority and cached in the library), with a draggable playhead and a larger preview of the frame under it. Press M to mark there. Arrow keys step one frame, Shift+arrow steps one second.

For each segment:

- Name (with quick picks: Intro, Verse 1 to 4, Pre-Chorus, Chorus, Bridge, Tag, Instrumental, Outro).
- Start time, editable as `mm:ss.fff`, with nudge buttons for plus and minus one frame and plus and minus 100 ms.
- Colour, from a fixed palette of about ten colours that read well on the dark background.
- Lyric note, one or two lines.
- Delete and reorder (reordering changes nothing on its own because segments sort by start time, so reorder means editing times).

Thumbnails are generated with ffmpeg at the segment's exact start time, scaled to 480px wide, saved as JPEG at quality 85, and regenerated when the start time changes. Run ffmpeg at below-normal process priority, one job at a time, off the UI thread. If ffmpeg fails, show a placeholder with the segment colour and name and log the error. The song still saves.

ffmpeg needs to read the source file. When the app runs on the Resolume PC, the path from Resolume works directly. When it's remote, the path Resolume reports is local to the Resolume PC, so apply the path mappings from settings before calling ffmpeg. If the mapped file can't be found, say so plainly in Edit mode and suggest either adding a mapping or doing the edit on the Resolume PC.

Show mode never runs ffmpeg. It only reads cached thumbnails from the library.

## Setlists

- Create, rename, duplicate and delete setlists.
- Add songs from the library, drag to reorder, remove.
- One setlist is active at a time, picked from a dropdown in Show mode.
- Show mode works without a setlist. It just follows whatever clip is connected.

## Out of scope for this build

These are likely later, so keep the trigger logic behind an internal interface (something like `ISegmentController` with Cut, Queue, ClearQueue and LaunchSong) so new inputs can call it without touching the UI code.

- ProPresenter follow: jump to the segment whose name matches the slide group the lyrics operator just fired, and pull lyric notes from the presentation.
- A local HTTP or OSC control endpoint for Stream Deck via Bitfocus Companion.
- Loop hold on a segment.
- Bar-quantised triggers using BPM.
- Any kind of multi-controller locking.

## Stages

Each stage ends with a short test list. Don't start the next stage until those pass on the Resolume PC.

### Stage 1: Resolume spike

A throwaway test window (or console app) that proves the integration before any real UI exists. Write the findings to `docs/RESOLUME_NOTES.md` in the repo, because the rest of the build depends on them.

- Connect to the WebSocket on a configurable host and port.
- Parse the composition and list every clip with layer, column, name and source file path.
- Show which clip is connected on each layer and update live.
- Subscribe to the connected clip's position and log how often updates arrive, at normal and fast playback.
- Read the position parameter's min, max and units.
- Seek to a typed time and measure how long until the position update confirms it.
- Test the connect-then-seek case: connect a clip that isn't playing and immediately seek to 60 s. Try seek-before-connect and connect-before-seek, check the clip's own trigger and retrigger settings, and note which order lands on the right frame without flashing the clip's first frame on output.
- Run the same tests from a second PC over VLAN 131.

Tests: the clip list matches the Resolume deck; seeks land within one frame; update rate and seek latency are written down for local and remote; the connect-and-seek order is known.

### Stage 2: core

- Settings load and save.
- Connection manager with the reconnect behaviour above, raising events for composition, connected clip and position.
- Song library load, save, safe writes and corrupt-file handling.
- Song-to-clip matching, including path mappings and the duplicate warning.
- Logging.

Tests: kill and restart Resolume while the app is connected and it recovers without intervention; unplug the network on the remote PC for 30 seconds and it recovers; a hand-broken JSON file is skipped with a warning; moving a clip to a different column in Resolume doesn't break its match.

### Stage 3: Show mode with cut

- Full Show mode layout, status bar, cards, whole-clip progress bar and segment progress bars.
- Auto-switching to the connected song.
- Cut triggering by click and by keys.
- Seek verification warnings.
- Follow role.

Hand-write two or three song JSON files for testing, since Edit mode doesn't exist yet.

Tests: cutting between segments during playback is instant and lands on the right frame; Follow mode can't fire anything; disconnecting disables the cards and the light goes red.

### Stage 4: queue

- Queue triggering, the one-slot queue, countdown, Esc, and the default trigger setting with Shift swap.
- Playhead estimation between updates and latency offset per host.
- Queue cleared on disconnect.

Tests: queuing Chorus during a verse jumps at the verse's end within a frame or two after tuning the offset, locally and remotely; queuing the natural next segment sends nothing; pausing the clip holds the queue.

### Stage 5: Edit mode

- New song from the composition or a file.
- Mark at Resolume playhead.
- Filmstrip scrubber.
- Segment editing, colours, lyric notes.
- Thumbnail generation with ffmpeg, including path mapping and failure placeholders.

Tests: build a full song from scratch in rehearsal using Mark at playhead; build one with the scrubber; thumbnails match the frames Resolume shows at those times; editing on the remote PC works with a mapping and fails clearly without one; Resolume output is untouched the whole time.

### Stage 6: setlists and song launch

- Setlist management and the setlist strip.
- Launch songs from setlist, using the connect-and-seek order from Stage 1.
- Left, Right and Enter.

Tests: run a mock service of five songs from a setlist with launch on, then again with launch off, and Segment Deck follows correctly in both.

### Stage 7: hardening

- Run it for three hours on the show PC with Resolume playing and check memory, CPU and GPU use stay flat.
- Tidy error messages so they're readable by a volunteer.
- README covering setup, the webserver setting in Resolume, firewall rules, path mappings, roles and the one-Controller rule.
- Self-contained publish.

## Security note for the README

Resolume's webserver has no authentication. Anyone who can reach port 8080 can control the composition. When Segment Deck runs remotely, add a Windows Firewall rule on the Resolume PC that allows inbound TCP 8080 only from the operator PC's IP address, and keep both machines on VLAN 131, away from the staff VLAN.
