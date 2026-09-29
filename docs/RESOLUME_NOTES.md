# Resolume integration notes (Stage 1 findings)

Everything below was measured against **Resolume Arena 7.24.1 rev 61371** on Windows 11, with the
probe and Arena on the **same PC** (127.0.0.1). The remote (VLAN 131) column is still to be filled in
by running the same probe from the operator PC. See "How to re-run" at the end.

The official references used: the WebSocket page at
https://resolume.com/support/en/websocket-api and the OpenAPI spec behind https://resolume.com/docs/restapi/
(`swagger.yaml`). Where the two disagree with what Arena actually does, this file records what Arena does.

## 1. Endpoints

| What | How | Verified |
|---|---|---|
| Product / version | `GET http://host:8080/api/v1/product` → `{"name":"Arena","major":7,"minor":24,"micro":1,"revision":61371}` | yes |
| Full composition | `GET /api/v1/composition` (110 KB for a 3-layer, 9-column comp) | yes |
| WebSocket | `ws://host:8080/api/v1` | yes |
| Connect (launch) a clip | WS `{"action":"trigger","parameter":"/composition/layers/L/clips/C/connect"}` or REST `POST /api/v1/composition/layers/L/clips/C/connect` | yes, both |
| Seek | WS `{"action":"set","parameter":"/parameter/by-id/<positionParamId>","value":<ms>}` or REST `PUT /api/v1/parameter/by-id/<id>` body `{"value":<ms>}` | yes, both |
| Set in-point | REST `PUT /api/v1/parameter/by-id/<positionParamId>` body `{"in":<ms>}` (WS `set` only writes `value`) | yes |
| Disconnect a layer (spike only) | `POST /api/v1/composition/layers/L/clear` | yes |
| Load a file into a slot (spike only) | `POST /api/v1/composition/layers/L/clips/C/open`, body `text/plain` `file:///C:/path/with%20spaces.mov` | yes |

Enable the server in Arena under **Preferences > Webserver**. On disk that is
`Documents\Resolume Arena\Preferences\server.xml`, `enabled="1" port="8080" address="0.0.0.0"`. No authentication.

## 2. WebSocket protocol as Arena 7.24 really speaks it

On connect the server sends three messages, in this order, within ~20 ms:

1. The **composition**: exactly the `GET /composition` JSON, no `type` field. Detect it by the presence of `layers`.
2. `{"type":"sources_update","value":{...}}`
3. `{"type":"effects_update","value":{...}}`

The composition message is **re-sent whenever the structure changes** (a file loaded into a slot, layers or
columns added) **and also every time a clip connects or disconnects** (seen in the Stage 2 app log: ~110 KB per
launch). It is *not* re-sent for plain parameter changes such as position. Parameter ids inside it stay the same
across these re-sends, so subscriptions survive; the app just re-parses and rebuilds its song matches.

Client → server (all confirmed working):

```json
{"action":"subscribe",   "parameter":"/parameter/by-id/1790598829047"}
{"action":"unsubscribe", "parameter":"/parameter/by-id/1790598829047"}
{"action":"get",         "parameter":"/parameter/by-id/1790598829047"}
{"action":"set",         "parameter":"/parameter/by-id/1790598829047", "value": 60000}
{"action":"set",         "parameter":"/parameter/by-id/1790598829075", "value": "Continue"}
{"action":"trigger",     "parameter":"/composition/layers/1/clips/1/connect"}
```

Server → client replies put the **parameter's own fields at the root of the message**, not nested
under `value`. The `value` field is the scalar:

```json
{"id":1790598829047,"valuetype":"ParamRange","min":0.0,"max":195239.979,"in":0.0,"out":195239.979,
 "value":127714.997,"path":"/composition/layers/1/clips/1/transport/position","type":"parameter_update"}

{"value":"Connected","valuetype":"ParamState","options":["Empty","Disconnected","Previewing","Connected","Connected & previewing"],
 "index":3,"id":1790598826956,"path":"/composition/layers/1/clips/1/connect","type":"parameter_update"}

{"path":"/composition/layers/1/clips/1/transport/position","error":"Invalid parameter path"}
```

`type` is one of `parameter_subscribed`, `parameter_unsubscribed`, `parameter_get`, `parameter_set`,
`parameter_update`. `trigger` gets no reply. Errors have no `type`, just `path` and `error`.
There is also an unsolicited `thumbnail_update` when a clip's thumbnail changes:
`{"value":{"size":69211,"last_update":"1790599011088","is_default":false,"id":<clipId>,"path":"/api/v1/composition/clips/by-id/<clipId>/thumbnail"},"type":"thumbnail_update"}`.

### Address parameters by id, not by path

- Logical paths work for clip-level parameters (`/composition/layers/1/clips/1/name`, `.../connected`).
- Logical paths **fail** for everything under `transport` (`.../transport/position`,
  `.../transport/controls/speed`, `.../transport/controls/playmode`) with `Invalid parameter path`.
  Arena's internal path for those is different (speed is really
  `/composition/layers/1/clips/1/transport/position/behaviour/speed`).
- `/parameter/by-id/<id>` works for all of them. The ids come from the composition JSON and are stable
  across sessions and reordering (per the spec; a clip keeps its ids when moved to another column).
- Replies always carry the internal path, so `.../connected` comes back as `.../connect`. Match replies on
  `id`, never on the path string.

**Rule for the app:** read ids from the composition, subscribe/set/get by id, match replies by id.
Re-read ids every time a composition message arrives.

## 3. Units and values

| Parameter | Where in the clip JSON | Unit / values |
|---|---|---|
| Position | `transport.position` (ParamRange) | **milliseconds**. `min` 0, `max` = file duration in ms (195239.979 for a 3:15 clip). `in`/`out` are the clip's in and out points, same unit. |
| File duration | `video.fileinfo.duration_ms` | ms (195240). `duration` is a `hh:mm:ss.ff` string. |
| Frame rate | `video.fileinfo.framerate` | `{num, denom}` ratio (25/1) |
| File path | `video.fileinfo.path` | plain Windows path, `C:\Users\...\file.mov`; `exists` says whether Arena can read it |
| Speed | `transport.controls.speed` (ParamRange) | 0..10, 1 = normal |
| Play direction | `transport.controls.playdirection` (ParamChoice) | `"<"`, `"||"` (paused), `">"` |
| Play mode | `transport.controls.playmode` | Loop, Bounce, Random, Play Once & Clear, Play Once & Hold |
| Retrigger behaviour | `transport.controls.playmodeaway` | **Restart** (default), **Continue**, Relative. See section 6. |
| Transport duration | `transport.controls.duration` | seconds (195.24), max 604800. Not needed. |
| Connected state | `connected` (ParamState) | Empty, Disconnected, Previewing, Connected, Connected & previewing. "Live on output" = starts with `Connected`. |
| Clip id | `id` | int64, stable |

The app still reads `min`/`max` and converts, as the brief asks, but the answer on 7.24 is ms in, ms out.

## 4. Position stream

- Subscribing to a position parameter streams `parameter_update` at a steady **100 Hz (every 10 ms)**
  while the clip plays, regardless of speed (measured at 1× and 2×: 100.0 Hz both, interval min 7.4 / max 12.6 ms).
  Each update is ~225 bytes, so ~22 KB/s per watched clip. Subscribe to one clip at a time.
- **Paused (`playdirection` = `||`): the stream goes completely silent.** No updates for 2 s. The app must
  not treat silence as a fault; it should subscribe to `playdirection` and treat `||` as paused.
- **Disconnected clips keep their transport running.** After the layer was cleared, position updates
  continued at 100 Hz and the playhead kept advancing. Never infer "live" from the stream; use `connected`.
- `connected` changes arrive ~3 ms after the trigger is sent.

## 5. Seek latency (same PC)

Seek = write the position parameter. The reply echoes the exact value written (landing offset 0.0 ms every time).

| Method | Confirmed by the next position update after | Notes |
|---|---|---|
| WS `set` `/parameter/by-id` | 0.3 – 0.7 ms (10/10) | use this |
| WS `set` logical path | never | `Invalid parameter path` |
| REST `PUT /parameter/by-id` | 0.5 – 0.9 ms (5/5) | fallback; first request on a cold HttpClient took 2 s |
| REST `PUT .../clips/C` partial body | 0.5 – 2.6 ms (3/3) | works, no reason to use |

So on the same PC the seek itself is effectively instant. Remote numbers will be network RTT plus a
few hundred microseconds; the brief's 250 ms / 500 ms verification window has plenty of headroom. OSC is not needed.

Two gotchas that cost 2 seconds each:

- `localhost` resolves to `::1` first and Arena only listens on IPv4, so the first connection waits for the IPv6
  attempt to time out. Use `127.0.0.1` in the default settings, or resolve and prefer IPv4.
- The first REST call on a fresh `HttpClient` is slow for the same reason. Warm it up at connect time.

## 6. Connect-and-seek order (the important one)

Setting: a clip that is not playing, target 60 s. Measured from the moment the first command is sent.
Position values are what Arena reported, every 10 ms.

With the clip's default retrigger setting (`playmodeaway` = **Restart**):

| Order | What happened |
|---|---|
| A: connect, then seek immediately | seek echoed at +1 ms, then the transport **reset to 0 at +5..13 ms** and played from the start. Seek lost. |
| B: seek, then connect | same: position sat at 60 s until the connect reset it to 0. |
| B2: seek, wait 300 ms, connect | same. |
| D: connect, wait for the `connected` update, then seek | `connected` arrives *before* the reset, so the seek is still lost. |
| E: REST connect, REST seek | same as A. |
| C: connect, wait 100 ms, seek | works, but the clip's first 90–100 ms are on output first. |
| **G: connect, wait for the position update that reports the reset (value < 1 s), then seek** | works; reset seen at +8..13 ms, seek echoed within 1 ms of it. The transport reported 0 for one 10 ms tick. At most one output frame of the clip start. |
| **F: set the position `in` point to 60 s (REST PUT `{"in":60000}`), connect, restore `in` to 0 after ~500 ms** | works; the transport **never reported the clip start**. Restoring `in` while playing did not move the playhead. |

With the clip's retrigger setting changed to `playmodeaway` = **Continue** (set with a WS `set` on that param's id):

| Order | What happened |
|---|---|
| **H: seek, wait 50 ms, connect** | works; the transport never reported the clip start. Connect did not disturb the position at all. |
| **I: connect, then seek immediately** | works; same. |

With `playmodeaway` = **Relative**: connect resets to 0 (plus an offset). Not useful.

### Decision for Stage 6

1. If the song clip's `playmodeaway` is `Continue`: **seek first, then connect** (order H). Nothing else needed.
   The README will tell the team to set the song clips' transport retrigger option to "Continue" in Resolume
   (the Restart / Continue / Relative choice in the clip's transport settings; API name `playmodeaway`) and
   save the composition. Segment Deck reads the value from the composition, so it can tell which order to use per clip.
2. Otherwise: **order F** (set `in`, connect, restore `in`) as the automatic fallback. It is flash-free with default
   clip settings. The only cost is that if Segment Deck died between the two PUTs, the clip would keep a
   non-zero in point until someone fixed it; restore it as soon as the first position update ≥ target arrives, and
   log both writes.
3. Order G is the no-side-effects alternative if F ever misbehaves: one 10 ms window where the start frame might be visible.

Launching a song from segment 1 (0 ms) needs no seek at all; just connect.

## 6b. Reconnect behaviour (Stage 2 integration test, same PC)

Killing Arena while connected: the socket reports closed within ~200 ms. Retrying every 2 s, the app reconnected
~5 s after Arena was relaunched, before Arena had finished loading its composition: the first composition message
after a cold start can have an empty name and empty slots, and Arena re-sends the composition when the real one loads.
Treat every composition message as authoritative and rebuild matches each time; do not assume the first one is final.

## 6c. Resource use (Stage 7, dev PC, 10 minutes, app minimised, clip playing at 100 Hz updates)

| minute | working set MB | private MB | CPU % of machine (16 cores) |
|---|---|---|---|
| 1 | 133 | 82 | 0.10 |
| 4 | 182 | 140 | 0.29 |
| 10 | 184 | 142 | 0.08 |

Flat from minute 4 on. The early growth is the WPF and GC heaps warming up, not a leak, but the 3-hour run on the show
PC is the real test.

## 7. Clip matching data available

Every clip gives `id`, `name.value`, `video.fileinfo.path` (absolute, as Arena sees it) and `video.fileinfo.exists`.
Layer index and column index are the array positions (1-based) in `layers[]` and `layers[].clips[]`.
Empty slots are present in the arrays with no `video` and an empty name; skip them.

## 8. Measurements still to take on the show PC and over VLAN 131

Run the probe from each machine and paste the report into `docs/probe-reports/`:

| Measurement | Same PC (done) | Show PC local | Remote over VLAN 131 |
|---|---|---|---|
| WS connect time | 13 ms (127.0.0.1) | | |
| Composition arrival after connect | 17 ms | | |
| Position update rate at 1× / 2× | 100 Hz / 100 Hz | | |
| Seek confirm latency, WS by-id (min/avg/max) | 0.3 / 0.4 / 0.7 ms | | |
| Seek confirm latency, REST by-id | 0.5 / 0.7 / 0.9 ms | | |
| Order H (Continue) flash-free | yes (by transport data) | eyes on output | eyes on output |
| Order F (in-point) flash-free | yes (by transport data) | eyes on output | eyes on output |
| Order G start-frame window | ≤ 10 ms | eyes on output | |

"Eyes on output" means someone watches the projector while the spike window runs the order buttons.

## How to re-run

Build once on a machine with the .NET 8 SDK, or use the published folder:

```bash
dotnet build SegmentDeck.sln -c Release
```

Console probe (unattended, writes `probe-<timestamp>.txt` in the current folder):

```bash
spike/SegmentDeck.Probe/bin/Release/net8.0/win-x64/SegmentDeck.Probe.exe --host 127.0.0.1 --layer 1 --column 1
```

Add `--open "D:\Media\Songs\Song.mov"` to load a file into an empty slot first (in memory only; do not save the
composition afterwards unless you want it). `--host 10.131.x.x` from the operator PC gives the remote numbers.
The probe clears the target layer between connect tests, so run it only when nothing is on output.

Interactive window (for the eyes-on-output checks): `spike/SegmentDeck.Spike/bin/Release/net8.0-windows/win-x64/SegmentDeck.Spike.exe`.
Connect, select the song clip in the grid, then use the A/B/C/G/F buttons while watching the output. Its log
is in `%AppData%\SegmentDeck\logs\spike-<date>.log`.
