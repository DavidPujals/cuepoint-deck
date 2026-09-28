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
| 1. Resolume spike | Built and measured on a local Arena 7.24.1. Findings in `docs/RESOLUME_NOTES.md`. Waiting on the show PC and VLAN 131 runs. |
| 2. Core | not started |
| 3. Show mode with cut | not started |
| 4. Queue | not started |
| 5. Edit mode | not started |
| 6. Setlists and song launch | not started |
| 7. Hardening | not started |

## Layout

```
SegmentDeck.sln
spike/
  SegmentDeck.Spike/     Stage 1 test window (WPF). Throwaway, but its Resolume/ folder is the seed for Stage 2.
  SegmentDeck.Probe/     Stage 1 console probe. Runs the measurement checklist unattended and writes a report.
docs/
  RESOLUME_NOTES.md      What Arena 7.24 actually does on the wire, with numbers. Read this before touching the integration.
  probe-reports/         Saved probe output per machine.
```

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
