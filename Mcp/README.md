# FamiStudio MCP Server

An [MCP](https://modelcontextprotocol.io) server that lets Claude (and other agents) **author, inspect, render, and live-drive FamiStudio**.

It has two halves:

| Half | Where | What it does |
|------|-------|--------------|
| **MCP server** | `Mcp/FamiStudioMcp` (this folder) | A standalone .NET console app that speaks MCP over stdio and exposes the tools below. |
| **Embedded control server** | `FamiStudio/Source/App/Common/Mcp/McpServer.cs` | A small HTTP listener compiled into FamiStudio itself, enabled with `-mcpserver`. It lets the MCP server drive the *running* app (play/stop/seek/load). |

Stateless tools (read/write/describe/render/export) work without a running app — they shell out to FamiStudio's command-line export. Live tools (`fs_*`) require FamiStudio to be running with `-mcpserver`.

```
Claude ──stdio──> famistudio-mcp ──┬── shell out ──> FamiStudio CLI        (stateless: files, audio, NSF)
                                    └── HTTP ───────> FamiStudio -mcpserver (live: play/stop/seek/load)
```

## Build

```bash
cd Mcp/FamiStudioMcp
dotnet build -c Release
```

The output is `bin/Release/net8.0/famistudio-mcp.dll`, run with `dotnet famistudio-mcp.dll`.

## Configuration

Two environment variables:

| Variable | Purpose | Example |
|----------|---------|---------|
| `FAMISTUDIO_BIN` | Path to the FamiStudio executable, used for stateless CLI operations. | `/Applications/FamiStudio.app/Contents/MacOS/FamiStudio` |
| `FAMISTUDIO_MCP_URL` | Base URL of the running app's control server. | `http://127.0.0.1:8675` (default) |

## Register with Claude Code / Claude Desktop

Add to your MCP client config (e.g. Claude Desktop `claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "famistudio": {
      "command": "dotnet",
      "args": ["/absolute/path/to/Mcp/FamiStudioMcp/bin/Release/net8.0/famistudio-mcp.dll"],
      "env": {
        "FAMISTUDIO_BIN": "/Applications/FamiStudio.app/Contents/MacOS/FamiStudio",
        "FAMISTUDIO_MCP_URL": "http://127.0.0.1:8675"
      }
    }
  }
}
```

## Enabling live control

Launch FamiStudio with the `-mcpserver` flag (optionally a custom port):

```bash
# macOS, default port 8675
/Applications/FamiStudio.app/Contents/MacOS/FamiStudio -mcpserver

# custom port
/Applications/FamiStudio.app/Contents/MacOS/FamiStudio -mcpserver:9000
```

A single `-mcpserver` argument does **not** trigger FamiStudio's command-line/export mode, so the normal GUI launches with the control server attached. The server binds to loopback only (`127.0.0.1`).

## Tools

### Stateless (no running app required)

| Tool | Description |
|------|-------------|
| `read_project(path)` | Read any project/song (`.fms`, `.txt`, `.ftm`, `.nsf`, `.mid`) and return it as FamiStudio Text. |
| `write_project(path, text)` | Write FamiStudio Text to disk and validate it by round-tripping through FamiStudio's parser. |
| `describe_project(path)` | Structured summary: songs (name, length, BPM, channels) and instruments. |
| `render_audio(path, outputPath, song, sampleRate, loopCount)` | Render a song to WAV so you can hear it. |
| `export_nsf(path, outputPath)` | Export to NSF (native NES sound format). |

### Live (require `FamiStudio -mcpserver`)

| Tool | Description |
|------|-------------|
| `fs_health()` | Is the control server reachable? App version, project loaded?, playing? |
| `fs_state()` | Transport state: playing, current frame, song index/name, song count, BPM. |
| `fs_open(path)` | Open a project file in the running app (no save prompt). |
| `fs_new()` | New empty project in the running app. |
| `fs_play()` / `fs_play_from_start()` / `fs_stop()` | Transport control. |
| `fs_seek(frame)` | Jump playback to a frame. |
| `fs_select_song(index)` | Switch the active song. |
| `fs_get_text()` | Serialize the *live* project (including unsaved edits) to FamiStudio Text. |
| `fs_apply_text(text)` | Replace the live project with one parsed from FamiStudio Text — push an authored song into the app to see/hear it instantly. |
| `fs_save(path)` | Save the live project (`.fms` native or `.txt`). |
| `fs_describe()` | Summary of the live project. |

### Semantic authoring — live (require `FamiStudio -mcpserver`)

Higher-level "write music" tools that edit the running project and appear instantly.

| Tool | Description |
|------|-------------|
| `fs_list_channels(song)` | Channel names to use with `fs_add_melody` (e.g. `Square1`, `Square2`, `Triangle`, `Noise`, `DPCM`). |
| `fs_list_instruments()` | Instruments in the project. |
| `fs_add_melody(notes, channel, song, startRow, instrument, replace)` | Add a melody using the note DSL (below). |
| `fs_clear_channel(channel, song, fromRow, toRow)` | Erase a row range. |
| `fs_set_tempo(bpm, song, notesPerBeat?)` | Best-effort BPM; returns the achieved BPM. |
| `fs_set_famistudio_tempo(groove[], song, notesPerBeat?)` | Exact FamiStudio-tempo groove. |
| `fs_set_famitracker_tempo(speed, tempo, song)` | FamiTracker speed/tempo. |
| `fs_add_instrument(name, expansion, preset?, volumeEnvelope?, dutyEnvelope?)` | Create an instrument; presets: `lead`, `bass`, `pad`, `pluck`, `blip`. |

### Semantic authoring — offline (no running app)

| Tool | Description |
|------|-------------|
| `apply_semantic_ops(inputPath, outputPath, ops)` | Apply a JSON batch of ops to a file and save. `ops` is `[{ "op": <name>, "args": {...} }, …]` using the same names/args as the `fs_*` tools. |

#### Note DSL (`add_melody`)

Space-separated tokens:

| Token | Meaning |
|-------|---------|
| `C4:4` | Note `C4` for 4 rows (`C#4`/`Db4` for accidentals; octave required) |
| `R:2` | Rest for 2 rows |
| `-` | Extend the previous note by one row |
| `^` | Note-off / stop at the current row |

Example: `"C4:4 E4:4 G4:2 R:2 G4:8"`. Durations are in note-rows (pattern time units).

Offline batch example:

```json
[
  { "op": "add_instrument", "args": { "name": "Lead", "preset": "lead" } },
  { "op": "set_tempo",      "args": { "bpm": 140 } },
  { "op": "add_melody",     "args": { "notes": "C4:4 E4:4 G4:4 C5:4", "channel": "Square1", "instrument": "Lead" } }
]
```

## Typical agent loop

1. `fs_get_text()` → read the current song as text.
2. Edit the text (add a melody, change an instrument, etc.).
3. `fs_apply_text(text)` → push it into the running app.
4. `fs_play_from_start()` → hear it.
5. `fs_save("/path/song.fms")` → persist.

Or fully offline: `write_project` → `render_audio` → listen.

## Control-server HTTP protocol

For reference, the embedded server accepts `POST http://127.0.0.1:<port>/<command>` with an optional JSON body. Responses are `{ "ok": true, "result": {...} }` or `{ "ok": false, "error": "..." }`. Commands: `health`, `get_state`, `play`, `play_from_start`, `stop`, `seek`, `select_song`, `new_project`, `open_project`, `save_project`, `get_project_text`, `load_text`, `describe_project`.
