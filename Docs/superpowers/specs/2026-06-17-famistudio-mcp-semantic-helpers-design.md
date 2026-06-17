# FamiStudio MCP — Semantic Authoring Helpers (v2) — Design

**Date:** 2026-06-17
**Status:** Proposed (builds on `feature/mcp-server`, PR #1)
**Depends on:** v1 MCP (embedded control server + standalone MCP server)

## Goal

Let agents author music at a musical altitude — "add this melody to the lead channel", "set it to 140 BPM", "give me a bass instrument" — instead of hand-writing FamiStudio Text. Higher-level tools that compose into the existing model.

## Decisions (locked in brainstorming)

| Topic | Decision |
|-------|----------|
| Where ops run | **Both**: a shared semantic core, used by live model ops (embedded server) *and* offline text authoring (MCP server). |
| Melody input | **Compact string DSL** (`"C4:4 E4:4 R:2"`). |
| Tempo | **All three**: `set_tempo(bpm)` best-effort + raw `set_famistudio_tempo` + `set_famitracker_tempo`; always report achieved BPM. |
| Instruments | **Presets + optional raw envelopes.** |

## Architecture: one engine, two callers

The core problem with "both offline and live" is avoiding two implementations. Solution: a single **`SemanticEngine`** in the FamiStudio assembly that operates *only* on an in-memory `Project`. It knows nothing about HTTP or MCP.

```
                 ┌────────────────────────── FamiStudio assembly ──────────────────────────┐
 live:  MCP fs_* ─HTTP─> McpServer ──> SemanticEngine.Apply(liveProject, op) ──> running app updates
 offline: MCP ──CLI/lib─> headless  ──> SemanticEngine.Apply(loadedProject, op) ──> FamistudioTextFile.Save
                 └──────────────────────────────────────────────────────────────────────────┘
```

- **`SemanticEngine`** (`FamiStudio/Source/App/Common/Mcp/SemanticEngine.cs`): pure functions `(Project, args) -> result`, using the real model APIs. Validity is guaranteed by the model; serialization round-trips for free.
- **Live path:** new control-server commands (`add_melody`, `set_tempo`, …) call `SemanticEngine` against `famistudio.Project` on the UI thread, then `MarkEverythingDirty()` / refresh so edits appear instantly.
- **Offline path:** the standalone MCP server gets matching stateless tools. Two implementation options for offline (decide in planning):
  - (a) **CLI subcommand** — add a `semantic-apply` command to `CommandLineInterface` that loads a file, runs `SemanticEngine`, saves. MCP shells out. No new dependency surface.
  - (b) **Shared library** — extract model + `SemanticEngine` into a class library the MCP references directly. Cleaner calls, larger refactor.
  Recommendation: **(a)** for v2 — smallest change, reuses the proven CLI path.

## The melody DSL

Grammar (whitespace-separated tokens):

```
token   := note ":" duration | "R" ":" duration | "-" | "^"
note     := letter [ "#" | "b" ] octave        ; e.g. C4, F#3, Eb5
duration := integer                              ; in note-rows (pattern time units)
R        := rest
-        := extend/sustain previous note by 1 row (no new note)
^        := release the current note
```

Example: `"C4:4 E4:4 G4:2 R:2 G4:8"` → C4 for 4 rows, E4 for 4, G4 for 2, rest 2, G4 for 8.

Mapping to the model:
- `Note.FromFriendlyName("C4")` → note value; reject `0xff` (invalid) with a clear error citing the offending token.
- A `Note` is created via `Pattern.GetOrCreateNoteAt(time)`, setting `Value`, `Instrument`, and `Duration` (FamiStudio stores explicit per-note durations — `Note.Duration`).
- `time` advances by each token's duration. The engine maps absolute song time to (patternIndex, timeInPattern) using the song's `NoteLength`/`PatternLength`, creating pattern instances along the channel as needed (`Channel.CreatePattern` + assign to `PatternInstances[patternIndex]`).
- `R` advances time without a note. `-` extends the prior note's `Duration`. `^` sets a release.

### `add_melody` signature

```
add_melody(
  notes:        string,            // the DSL
  channel:      string,            // channel name, e.g. "Square1" (ChannelType.InternalNames)
  song:         int = 0,
  startRow:     int = 0,           // absolute row in the song to begin
  instrument:   string? = null,    // instrument name; default = first instrument
  replace:      bool = false       // clear existing notes in the affected range first
) -> { ok, channel, song, notesWritten, endRow }
```

Channel name → type via `ChannelType` (reverse lookup of `InternalNames`); error lists valid names for the project's expansion.

## Tempo tools

FamiStudio tempo is groove-based; the engine mirrors what `TempoProperties.cs` does in the UI.

```
set_tempo(bpm: float, song: int = 0, notesPerBeat: int? = null)
  -> { ok, requestedBpm, achievedBpm, groove, notesPerBeat, mode }
```
- Determine `notesPerBeat` (default from the song's current beat/note length).
- `var tempos = FamiStudioTempoUtils.GetAvailableTempos(project.PalMode, notesPerBeat);`
- Pick the `TempoInfo` whose BPM (`FamiStudioTempoUtils.ComputeBpmForGroove`) is closest to `bpm`.
- Apply its groove + note length + beat length to the song using the same path as the Tempo dialog.
- For FamiTracker-tempo songs, translate to nearest `speed`/`tempo` instead.
- Return `achievedBpm` so the agent knows if the target was approximated.

```
set_famistudio_tempo(groove: int[], song: int = 0, notesPerBeat: int? = null, groovePadMode: string = "middle")
  -> { ok, achievedBpm, groove }
```
- `FamiStudioTempoUtils.ValidateGroove(groove)` then apply. Achieved BPM via `ComputeBpmForGroove`.

```
set_famitracker_tempo(speed: int, tempo: int, song: int = 0)
  -> { ok, speed, tempo }
```
- Sets `Song.FamitrackerSpeed` / `Song.FamitrackerTempo` (only valid for FamiTracker-tempo projects; error otherwise).

## Instrument tools

```
add_instrument(
  name:            string,
  expansion:       string = "none",     // none|vrc6|vrc7|fds|mmc5|n163|s5b|epsm (ExpansionType)
  preset:          string? = null,      // lead|bass|pad|pluck|blip
  volumeEnvelope:  int[]? = null,       // raw, overrides preset's volume
  dutyEnvelope:    int[]? = null        // raw, overrides preset's duty
) -> { ok, name, expansion, preset, volumeEnvelope, dutyEnvelope }
```

- Create via `Project.CreateInstrument(ExpansionType.<x>, name)`.
- Presets fill `Instrument.Envelopes[EnvelopeType.Volume]` and `[EnvelopeType.DutyCycle]` by setting `Envelope.Length` and writing `Envelope.Values` (`sbyte[]`), clamped to each envelope's valid range (`Envelope.GetEnvelopeMaxLength` / `Note.VolumeMax`). Starter presets:
  - `lead`  — volume `[15,15,14,12,10,8,6,4]`, duty `[2]` (50%)
  - `bass`  — volume `[15,15,13,11,9,7]`, duty `[0]` (12.5%)
  - `pad`   — volume `[8,10,12,12,10,8]`, duty `[2]`
  - `pluck` — volume `[15,10,6,3,1]`, duty `[1]`
  - `blip`  — volume `[15,8,2]`, duty `[2]`
- Raw `volumeEnvelope`/`dutyEnvelope` override the preset; values out of range are rejected with the valid range in the error.

## Supporting tools (small, high value)

- `list_channels(song=0)` → channel names + types for the project's expansion (so the agent picks valid channel names).
- `list_instruments()` → names + expansions.
- `clear_channel(channel, song=0, fromRow?, toRow?)` → erase a range (pairs with `add_melody replace=false`).

## MCP tool surface (added)

Live (`fs_*`, proxy the new control-server commands): `fs_add_melody`, `fs_set_tempo`, `fs_set_famistudio_tempo`, `fs_set_famitracker_tempo`, `fs_add_instrument`, `fs_list_channels`, `fs_list_instruments`, `fs_clear_channel`.

Offline (stateless, operate on a file via the CLI subcommand): `add_melody`, `set_tempo`, `add_instrument`, `clear_channel` — each takes an input path and writes back (or to a new path).

## Real APIs this relies on (verified)

| Need | API | File |
|------|-----|------|
| note value ↔ name | `Note.FromFriendlyName` / `GetFriendlyName` | Note.cs:508,523 |
| per-note duration | `Note.Duration` | Note.cs:176 |
| place a note | `Pattern.GetOrCreateNoteAt(time)` / `SetNoteAt` | Pattern.cs:131,144 |
| create/place patterns | `Channel.CreatePattern`, `Channel.PatternInstances` | Channel.cs |
| channel names | `ChannelType.InternalNames` | Channel.cs:18 |
| BPM/groove math | `FamiStudioTempoUtils.GetAvailableTempos / ComputeBpmForGroove / FindTempoFromGroove / ValidateGroove` | TempoUtils.cs:31,123,106,170 |
| apply tempo (reference impl) | `TempoProperties.cs` | App/Common/Dialogs |
| famitracker tempo | `Song.FamitrackerSpeed` / `FamitrackerTempo` | Song.cs:56,57 |
| create instrument | `Project.CreateInstrument(expansion, name)` | Project.cs:485 |
| envelopes | `Instrument.Envelopes[EnvelopeType.*]`, `Envelope.Length`, `Envelope.Values` | Instrument.cs / Envelope.cs:23,51 |

## Out of scope (v3+)

- Effects/automation (vibrato, volume slides, pitch envelopes beyond duty/volume).
- DPCM sample assignment and drum kits.
- Arpeggio objects.
- Chord/polyphony helpers (NES channels are monophonic; multi-channel chord spread could be a future helper).
- Non-loopback / authenticated control.

## Verification plan

- Unit-level: DSL parser (valid/invalid tokens, durations, rests, extend/release) — pure and testable without the app.
- Build: fork builds with `SemanticEngine` + new commands; MCP server builds with new tools.
- Headless: `semantic-apply add_melody` on a copy of a demo song, then `famistudio-txt-export`, assert notes land on the right channel/rows.
- Tempo: `set_tempo(140)` then read back `ComputeBpmForGroove` ≈ 140; assert `achievedBpm` reported.
- Live (manual, on desktop): `fs_add_melody` → `fs_play_from_start`, confirm audible + visible in piano roll.
```
