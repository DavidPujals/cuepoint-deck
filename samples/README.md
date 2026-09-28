# Sample library

Point the **Library path** setting at `samples/library` (or copy its contents into your library folder) to try the app
without building songs first.

- `songs/…0001.json` matches the SHOUT FOR JOY DXV file used in the Stage 1 tests. Change `clip.filePath` to a file
  that is actually in your composition.
- `songs/…0002.json` points at a file that is not in the deck, so it shows as unavailable.
- `songs/broken-example.json` is deliberately invalid JSON. It must show up as a warning and nothing else.
- `setlists/2026-10-04 North AM.json` lists both songs.
