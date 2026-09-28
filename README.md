# Segment Deck

Segment Deck is a Windows app for Nova Church's visuals operator. It fires named sections of a song
(Verse 1, Chorus, Bridge…) inside a Resolume Arena clip by moving the clip's playhead, so a song can have
as many segments as it needs instead of Resolume's six cue points. Each segment shows a thumbnail and a
lyric note so the operator knows what they are about to fire.

Resolume stays in charge of playback. If Segment Deck crashes or loses its connection the clip keeps
playing and the operator falls back to Resolume's own controls. Nothing in the app can stop or pause output.

The build brief is in `docs/segment-deck-brief.md`. Work is staged; each stage is tested on a real
Resolume machine before the next one starts.

## Status

| Stage | State |
|---|---|
| 1. Resolume spike | Built and measured on a local Arena 7.24.1. Findings in `docs/RESOLUME_NOTES.md`. Show PC and VLAN 131 probe runs still to do. |
| 2. Core | Built. Settings, logging, connection manager with 2 s reconnect, song library with safe writes and conflict detection, song-to-clip matching with path mappings. 18 unit tests plus an Arena kill-and-restart test pass locally. Needs the Stage 2 checks on the show PC. |
| 3. Show mode with cut | not started |
| 4. Queue | not started |
| 5. Edit mode | not started |
| 6. Setlists and song launch | not started |
| 7. Hardening | not started |

## Layout

```
SegmentDeck.sln
src/
  SegmentDeck.Core/      Everything that is not UI: settings, logging, Resolume connection, library, matching, ISegmentController.
  SegmentDeck.App/       The WPF app. At Stage 2 it is a diagnostics shell (status bar, live state, matches, settings, log).
tests/
  SegmentDeck.Core.Tests/  xunit tests, including recorded Arena messages as fixtures and an opt-in Arena reconnect test.
samples/library/         A tiny library with a matching song, an unavailable song and a broken file.
spike/
  SegmentDeck.Spike/     Stage 1 test window (WPF). Throwaway.
  SegmentDeck.Probe/     Stage 1 console probe. Runs the measurement checklist unattended and writes a report.
docs/
  RESOLUME_NOTES.md      What Arena 7.24 actually does on the wire, with numbers. Read this before touching the integration.
  probe-reports/         Saved probe output per machine.
```

## Build, test, run

```bash
dotnet build SegmentDeck.sln -c Release
```

```bash
dotnet test tests/SegmentDeck.Core.Tests
```

The Arena reconnect test only runs when `SEGMENTDECK_ARENA_TESTS=1` is set and Arena is installed. It kills and
relaunches Arena, so never run it on the show PC during a service.

Publish a self-contained folder to copy to another PC (no .NET install needed there):

```bash
dotnet publish src/SegmentDeck.App/SegmentDeck.App.csproj -c Release -o publish/SegmentDeck
```

Run `publish/SegmentDeck/SegmentDeck.exe`. On first start it uses `127.0.0.1:8080` and creates
`Documents\SegmentDeck Library`. Settings live in `%AppData%\SegmentDeck\settings.json`,
logs in `%AppData%\SegmentDeck\logs\` (daily files, kept 14 days).

### Stage 2 checks on the show PC

1. Start the app with Resolume running: the light goes green and the LIVE panel lists the composition.
2. Kill Resolume and start it again. The light goes amber, then green again on its own, and the composition is re-read.
3. On the remote PC, unplug the network for 30 seconds. Same result.
4. Copy `samples/library/songs/broken-example.json` into your library's `songs` folder and press Refresh: it appears
   as a warning, everything else still loads.
5. Move a song's clip to a different column in Resolume: the Clip column in the matches table follows it.

## Requirements

- Windows 10 or 11, 64-bit.
- Resolume Arena with the webserver enabled: **Preferences > Webserver**, default port 8080.
- .NET 8 SDK to build. The published app will be self-contained and need nothing installed.

## Build and run the Stage 1 tools

```bash
dotnet build SegmentDeck.sln -c Release
```

Console probe, from the Resolume PC:

```bash
spike/SegmentDeck.Probe/bin/Release/net8.0/win-x64/SegmentDeck.Probe.exe --host 127.0.0.1 --layer 1 --column 1
```

Use `--host <ip of the Resolume PC>` from the operator PC on VLAN 131. Use `--open "<path to a song file>"` to
load a clip into an empty slot first. The probe disconnects the target layer between its connect tests, so do
not run it during a service.

The interactive spike window is `SegmentDeck.Spike.exe` next to it. It lists the composition, streams the
live clip's position, seeks to a typed time and has one button per connect-and-seek order so someone can
watch the output for a flash of the clip's first frame.

## Security note

Resolume's webserver has no authentication. Anyone who can reach port 8080 can control the composition.
When Segment Deck runs on a different PC, add a Windows Firewall rule on the Resolume PC that allows
inbound TCP 8080 only from the operator PC's IP address, and keep both machines on VLAN 131, away from
the staff VLAN.

## One Controller at a time

Nothing detects or blocks two Controller instances talking to the same Resolume. Run one Controller; any
extra instances (producer's screen, FOH) must be set to the Follow role.
