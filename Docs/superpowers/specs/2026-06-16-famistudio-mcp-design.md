# FamiStudio MCP — Design

**Date:** 2026-06-16
**Status:** v1 implemented on branch `feature/mcp-server`

## Goal

Let Claude and other agents write music, view music, play, and generally control FamiStudio on macOS via MCP.

## Locked decisions

- **Interaction model:** both stateless generation *and* live control of the running app.
- **Live control transport:** embedded HTTP command server inside the FamiStudio fork (chosen over OS-level GUI automation and file-watch). Only this option can read true musical/transport state back.
- **Authoring contract:** FamiStudio Text format (round-trippable plaintext), reusing FamiStudio's own serializer/parser. No bespoke semantic music layer in v1.
- **MCP server language:** C#/.NET — FamiStudio already requires .NET, so no extra runtime for the user.
- **Delivery:** feature branch → PR to the fork.

## Architecture

Two processes:

```
Claude ──stdio──> famistudio-mcp ──┬── shell out ──> FamiStudio CLI        (stateless)
                                    └── HTTP ───────> FamiStudio -mcpserver (live)
```

### 1. Embedded control server (in the fork)

`FamiStudio/Source/App/Common/Mcp/McpServer.cs`

- Enabled only via `-mcpserver[:port]` (default port 8675). A lone `-mcpserver` keeps `args.Length < 3`, so `CommandLineInterface.HasAnythingToDo` stays false and the GUI launches normally.
- `HttpListener` on a background thread, bound to `127.0.0.1` only.
- **Thread safety:** requests are not executed on the listener thread. Each is queued to a `ConcurrentQueue` and executed on the UI/main thread from `FamiStudio.Tick()`, mirroring the existing `midiNoteQueue` / `ProcessQueuedMidiNotes()` pattern. This keeps all project/player access single-threaded, as the rest of the app assumes.
- Protocol: `POST /<command>` + optional JSON body → `{ "ok", "result" | "error" }`.

Integration points in `FamiStudio.cs`:
- field `mcpServer`
- `McpServer.TryStart(this, args)` after `Initialize(...)` in `Run()`, `Stop()` after the loop
- `mcpServer?.ProcessPendingCommands()` in `Tick()`
- prompt-free helpers `McpNewProject` / `McpOpenProjectFile` / `McpLoadProjectInstance` (skip the "save current project?" modal so agents aren't blocked)

Commands: `health`, `get_state`, `play`, `play_from_start`, `stop`, `seek`, `select_song`, `new_project`, `open_project`, `save_project`, `get_project_text`, `load_text`, `describe_project`.

### 2. MCP server (standalone)

`Mcp/FamiStudioMcp` — .NET 8 console app, `ModelContextProtocol` SDK, stdio transport.

- `FamiStudioConfig` — env resolution (`FAMISTUDIO_BIN`, `FAMISTUDIO_MCP_URL`).
- `FamiStudioCli` — shells out to the FamiStudio command-line app for stateless ops.
- `ControlClient` — HTTP client for the live control server.
- `FamiStudioText` — minimal FamiStudio-Text reader for structured summaries.
- `FamiStudioTools` — the 18 MCP tools (5 stateless + 13 live).

Stateless tools reuse the **existing** FamiStudio CLI export commands (`famistudio-txt-export`, `wav-export`, `nsf-export`), so no new export code was needed in the fork.

## Data flow examples

- **Author offline:** `write_project` (validate via round-trip) → `render_audio` → listen.
- **Live loop:** `fs_get_text` → edit text → `fs_apply_text` → `fs_play_from_start` → `fs_save`.

## Verification (this environment)

- Fork builds with embedded server: `dotnet build FamiStudio.Mac.csproj` → 0 errors.
- MCP server builds and advertises all 18 tools over the MCP protocol.
- Headless CLI text export of a demo song works.
- End-to-end `tools/call describe_project` returns correct structured data.
- Not verifiable headless: live playback tools (`fs_*`) require the GUI app running on a desktop — inherent to the feature.

## Out of scope (v2+)

- Semantic authoring helpers (`add_melody`, `set_tempo`, `add_instrument`) over the text contract.
- Live fine-grained note editing through the control server.
- Piano-roll image export for visual "viewing".
- Authentication / non-loopback binding.
```
