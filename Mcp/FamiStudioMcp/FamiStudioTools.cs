using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FamiStudioMcp;

// All tools exposed to MCP clients. Static methods discovered via WithToolsFromAssembly().
//
// Naming convention:
//   read_/write_/describe_/render_/export_  -> stateless, operate on files via the CLI
//   fs_*                                      -> live, drive a running FamiStudio over HTTP
[McpServerToolType]
public static class FamiStudioTools
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // ---------------------------------------------------------------------------------------
    // Stateless tools (file-based, via the FamiStudio command-line app; no running app needed)
    // ---------------------------------------------------------------------------------------

    [McpServerTool, Description(
        "Read a FamiStudio project (or any importable format: .fms, .txt, .ftm, .nsf, .mid) and " +
        "return its contents as FamiStudio Text — the round-trippable plaintext song format. " +
        "Use this to 'view' a song before editing it.")]
    public static async Task<string> read_project(
        [Description("Absolute path to the project/song file.")] string path)
    {
        if (!File.Exists(path))
            return Error($"File not found: {path}");

        // A .txt may already be FamiStudio Text; still round-trip it through the app so the
        // returned text is normalized and validated.
        return await FamiStudioCli.ExportTextAsync(path);
    }

    [McpServerTool, Description(
        "Write a song to disk in FamiStudio Text format and validate it by round-tripping through " +
        "FamiStudio's own parser. Returns whether the text is valid. Open the resulting .txt in " +
        "FamiStudio, or load it live with fs_apply_text.")]
    public static async Task<string> write_project(
        [Description("Absolute output path. Use a .txt extension for FamiStudio Text.")] string path,
        [Description("The full FamiStudio Text content to write.")] string text)
    {
        await File.WriteAllTextAsync(path, text);

        // Validate by re-exporting through the app: if it loads and re-emits, the text is valid.
        var verify = Path.Combine(Path.GetTempPath(), $"fs_mcp_{Guid.NewGuid():N}.txt");
        try
        {
            var result = await FamiStudioCli.RunAsync(new[] { path, "famistudio-txt-export", verify });
            var valid = result.Ok && File.Exists(verify);
            return Json(new
            {
                ok = valid,
                path,
                valid,
                log = (result.StdOut + result.StdErr).Trim(),
            });
        }
        finally
        {
            FamiStudioCli.TryDelete(verify);
        }
    }

    [McpServerTool, Description(
        "Summarize a project: songs (name, length, tempo/BPM, channel count) and instruments. " +
        "A compact structured 'view' without the full note data.")]
    public static async Task<string> describe_project(
        [Description("Absolute path to the project/song file.")] string path)
    {
        if (!File.Exists(path))
            return Error($"File not found: {path}");

        var text = await FamiStudioCli.ExportTextAsync(path);
        return Json(FamiStudioText.Describe(text));
    }

    [McpServerTool, Description(
        "Render a song to a WAV file so you can hear the result. Returns the output path.")]
    public static async Task<string> render_audio(
        [Description("Absolute path to the project/song file.")] string path,
        [Description("Absolute output .wav path.")] string outputPath,
        [Description("Song index to render (default 0).")] int song = 0,
        [Description("Sample rate in Hz (default 44100).")] int sampleRate = 44100,
        [Description("Number of times to play through the song (default 1).")] int loopCount = 1)
    {
        if (!File.Exists(path))
            return Error($"File not found: {path}");

        var result = await FamiStudioCli.RunAsync(new[]
        {
            path, "wav-export", outputPath,
            $"-export-song:{song}",
            $"-wav-export-rate:{sampleRate}",
            $"-wav-export-loop:{loopCount}",
        });

        if (!result.Ok || !File.Exists(outputPath))
            return Error($"WAV export failed (exit {result.ExitCode}).\n{result.StdOut}\n{result.StdErr}");

        return Json(new { ok = true, outputPath, sampleRate, song, loopCount });
    }

    [McpServerTool, Description(
        "Export a project to an NSF file (the native NES sound format playable on hardware/emulators).")]
    public static async Task<string> export_nsf(
        [Description("Absolute path to the project/song file.")] string path,
        [Description("Absolute output .nsf path.")] string outputPath)
    {
        if (!File.Exists(path))
            return Error($"File not found: {path}");

        var result = await FamiStudioCli.RunAsync(new[] { path, "nsf-export", outputPath });
        if (!result.Ok || !File.Exists(outputPath))
            return Error($"NSF export failed (exit {result.ExitCode}).\n{result.StdOut}\n{result.StdErr}");

        return Json(new { ok = true, outputPath });
    }

    // ---------------------------------------------------------------------------------------
    // Live tools (drive a running FamiStudio launched with "-mcpserver")
    // ---------------------------------------------------------------------------------------

    [McpServerTool, Description(
        "Check whether a running FamiStudio control server is reachable, and report app version " +
        "and whether a project is loaded/playing.")]
    public static Task<string> fs_health() => ControlClient.SendPrettyAsync("health");

    [McpServerTool, Description(
        "Get the live transport state of the running app: playing?, current frame, loaded project, " +
        "selected song index/name, song count, BPM.")]
    public static Task<string> fs_state() => ControlClient.SendPrettyAsync("get_state");

    [McpServerTool, Description("Open a project file in the running FamiStudio (no save prompt).")]
    public static Task<string> fs_open(
        [Description("Absolute path to a .fms/.txt/.ftm/.nsf/.mid file.")] string path)
        => ControlClient.SendPrettyAsync("open_project", new { path });

    [McpServerTool, Description("Create a fresh empty project in the running app (no save prompt).")]
    public static Task<string> fs_new() => ControlClient.SendPrettyAsync("new_project");

    [McpServerTool, Description("Start/resume playback from the current position.")]
    public static Task<string> fs_play() => ControlClient.SendPrettyAsync("play");

    [McpServerTool, Description("Play the current song from the beginning.")]
    public static Task<string> fs_play_from_start() => ControlClient.SendPrettyAsync("play_from_start");

    [McpServerTool, Description("Stop playback.")]
    public static Task<string> fs_stop() => ControlClient.SendPrettyAsync("stop");

    [McpServerTool, Description("Seek playback to a specific frame in the current song.")]
    public static Task<string> fs_seek(
        [Description("Target frame index.")] int frame)
        => ControlClient.SendPrettyAsync("seek", new { frame });

    [McpServerTool, Description("Select the active song in the running app by index (or 0-based position).")]
    public static Task<string> fs_select_song(
        [Description("Song index within the project.")] int index)
        => ControlClient.SendPrettyAsync("select_song", new { index });

    [McpServerTool, Description(
        "Serialize the project currently loaded in the running app to FamiStudio Text. " +
        "Use this to 'view' or capture the live project, including unsaved edits.")]
    public static Task<string> fs_get_text() => ControlClient.SendPrettyAsync("get_project_text");

    [McpServerTool, Description(
        "Replace the project in the running app with one parsed from FamiStudio Text. " +
        "This is how you push a song you authored into the live app to see and hear it immediately.")]
    public static Task<string> fs_apply_text(
        [Description("Full FamiStudio Text content to load.")] string text)
        => ControlClient.SendPrettyAsync("load_text", new { text });

    [McpServerTool, Description(
        "Save the running app's current project to disk. Use a .fms extension for the native " +
        "format or .txt for FamiStudio Text.")]
    public static Task<string> fs_save(
        [Description("Absolute output path (.fms or .txt).")] string path)
        => ControlClient.SendPrettyAsync("save_project", new { path });

    [McpServerTool, Description("Summarize the project currently loaded in the running app.")]
    public static Task<string> fs_describe() => ControlClient.SendPrettyAsync("describe_project");

    // ---------------------------------------------------------------------------------------

    private static string Json(object value) => JsonSerializer.Serialize(value, Indented);

    private static string Error(string message) => Json(new { ok = false, error = message });
}
