using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace FamiStudio
{
    // Lightweight HTTP control server that lets external agents (e.g. an MCP server) drive a
    // running FamiStudio instance: query state, control playback, and load/serialize projects.
    //
    // Design notes:
    //  - Enabled only when the app is launched with "-mcpserver" (optionally "-mcpserver:<port>").
    //    A single "-mcpserver" arg keeps args.Length < 3, so CommandLineInterface.HasAnythingToDo
    //    stays false and the normal GUI still launches.
    //  - The HttpListener runs on its own background thread. Requests are NOT executed there:
    //    every command is queued and run on the UI/main thread from FamiStudio.Tick(), mirroring
    //    the existing midiNoteQueue / ProcessQueuedMidiNotes() pattern. This keeps all access to
    //    the project model and players single-threaded, exactly like the rest of the app.
    //  - Protocol: POST (or GET) to http://127.0.0.1:<port>/<command>, optional JSON object body
    //    for arguments. Responses are { "ok": true, "result": {...} } or { "ok": false, "error": "..." }.
    public class McpServer
    {
        public const int DefaultPort = 8675;

        private readonly FamiStudio famistudio;
        private readonly int port;
        private HttpListener listener;
        private Thread listenerThread;
        private volatile bool running;

        private readonly ConcurrentQueue<McpRequest> pending = new ConcurrentQueue<McpRequest>();

        private class McpRequest
        {
            public string Command;
            public Dictionary<string, JsonElement> Args;
            public object Result;
            public string Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private McpServer(FamiStudio fs, int port)
        {
            this.famistudio = fs;
            this.port = port;
        }

        // Parses the command-line args and starts the server if "-mcpserver[:port]" is present.
        // Returns null when the flag is absent so the caller can simply assign the result.
        public static McpServer TryStart(FamiStudio fs, string[] args)
        {
            var port = DefaultPort;
            var enabled = false;

            foreach (var a in args)
            {
                if (a == "-mcpserver")
                {
                    enabled = true;
                }
                else if (a.StartsWith("-mcpserver:"))
                {
                    enabled = true;
                    if (int.TryParse(a.Substring("-mcpserver:".Length), out var p) && p > 0 && p < 65536)
                        port = p;
                }
            }

            if (!enabled)
                return null;

            var server = new McpServer(fs, port);
            server.Start();
            return server;
        }

        private void Start()
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                running = true;

                listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "McpServerListener" };
                listenerThread.Start();

                Console.WriteLine($"[MCP] FamiStudio control server listening on http://127.0.0.1:{port}/");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[MCP] Failed to start control server on port {port}: {e.Message}");
                running = false;
            }
        }

        public void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            try { listener?.Close(); } catch { }
        }

        private void ListenLoop()
        {
            while (running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext();
                }
                catch
                {
                    // Listener stopped or errored; exit loop if we're shutting down.
                    if (!running) break;
                    continue;
                }

                try { HandleContext(ctx); }
                catch (Exception e) { Console.WriteLine($"[MCP] Request error: {e.Message}"); }
            }
        }

        private void HandleContext(HttpListenerContext ctx)
        {
            var command = ctx.Request.Url.AbsolutePath.Trim('/').ToLowerInvariant();

            Dictionary<string, JsonElement> args = null;
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
                var body = reader.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try { args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body); }
                    catch { /* leave args null on malformed body */ }
                }
            }

            var req = new McpRequest { Command = command, Args = args ?? new Dictionary<string, JsonElement>() };

            pending.Enqueue(req);

            // Wait for the UI thread to process the command. Generous timeout for project loads.
            object payload;
            int status;
            if (req.Done.Wait(TimeSpan.FromSeconds(30)))
            {
                if (req.Error != null)
                {
                    payload = new Dictionary<string, object> { ["ok"] = false, ["error"] = req.Error };
                    status = 400;
                }
                else
                {
                    payload = new Dictionary<string, object> { ["ok"] = true, ["result"] = req.Result };
                    status = 200;
                }
            }
            else
            {
                payload = new Dictionary<string, object> { ["ok"] = false, ["error"] = "Timed out waiting for the UI thread." };
                status = 504;
            }

            var json = JsonSerializer.SerializeToUtf8Bytes(payload);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = json.Length;
            ctx.Response.OutputStream.Write(json, 0, json.Length);
            ctx.Response.OutputStream.Close();
        }

        // Called from FamiStudio.Tick() on the UI/main thread. Drains and executes queued commands.
        public void ProcessPendingCommands()
        {
            while (pending.TryDequeue(out var req))
            {
                try { req.Result = Dispatch(req.Command, req.Args); }
                catch (Exception e) { req.Error = e.Message; }
                finally { req.Done.Set(); }
            }
        }

        // Command dispatch. Runs on the UI thread, so it can freely touch the project and players.
        private object Dispatch(string command, Dictionary<string, JsonElement> args)
        {
            switch (command)
            {
                case "":
                case "health":
                    return new Dictionary<string, object>
                    {
                        ["app"] = "FamiStudio",
                        ["version"] = Platform.ApplicationVersion,
                        ["hasProject"] = famistudio.Project != null,
                        ["playing"] = famistudio.IsPlaying,
                    };

                case "get_state":
                    return GetState();

                case "play":
                    famistudio.PlaySong();
                    return GetState();

                case "play_from_start":
                    famistudio.PlaySongFromBeginning();
                    return GetState();

                case "stop":
                    famistudio.StopSong();
                    return GetState();

                case "seek":
                    famistudio.SeekSong(GetInt(args, "frame", 0));
                    return GetState();

                case "select_song":
                    SelectSong(args);
                    return GetState();

                case "new_project":
                    famistudio.McpNewProject();
                    return GetState();

                case "open_project":
                {
                    var path = GetString(args, "path", null);
                    if (string.IsNullOrEmpty(path)) throw new Exception("Missing 'path'.");
                    if (!File.Exists(path)) throw new Exception($"File not found: {path}");
                    famistudio.McpOpenProjectFile(path);
                    return GetState();
                }

                case "save_project":
                {
                    var path = GetString(args, "path", null);
                    if (string.IsNullOrEmpty(path)) throw new Exception("Missing 'path'.");
                    SaveProject(famistudio.Project, path);
                    return new Dictionary<string, object> { ["saved"] = path };
                }

                case "get_project_text":
                    return new Dictionary<string, object> { ["text"] = SerializeProjectText(famistudio.Project) };

                case "load_text":
                {
                    var text = GetString(args, "text", null);
                    if (string.IsNullOrEmpty(text)) throw new Exception("Missing 'text'.");
                    var project = LoadProjectFromText(text);
                    famistudio.McpLoadProjectInstance(project);
                    return GetState();
                }

                case "describe_project":
                    return DescribeProject(famistudio.Project);

                default:
                    throw new Exception($"Unknown command: '{command}'.");
            }
        }

        private Dictionary<string, object> GetState()
        {
            var project = famistudio.Project;
            var song = famistudio.SelectedSong;
            return new Dictionary<string, object>
            {
                ["playing"] = famistudio.IsPlaying,
                ["seeking"] = famistudio.IsSeeking,
                ["currentFrame"] = famistudio.CurrentFrame,
                ["projectName"] = project?.Name,
                ["songCount"] = project?.Songs.Count ?? 0,
                ["songIndex"] = (project != null && song != null) ? project.Songs.IndexOf(song) : -1,
                ["songName"] = song?.Name,
                ["songLength"] = song?.Length ?? 0,
                ["bpm"] = song?.BPM ?? 0f,
            };
        }

        private void SelectSong(Dictionary<string, JsonElement> args)
        {
            var project = famistudio.Project ?? throw new Exception("No project loaded.");
            var index = GetInt(args, "index", -1);
            if (index < 0)
            {
                var name = GetString(args, "name", null);
                if (name != null)
                    index = project.Songs.FindIndex(s => s.Name == name);
            }
            if (index < 0 || index >= project.Songs.Count)
                throw new Exception("Song not found (provide a valid 'index' or 'name').");
            famistudio.SelectedSong = project.Songs[index];
        }

        private Dictionary<string, object> DescribeProject(Project project)
        {
            if (project == null) throw new Exception("No project loaded.");

            var songs = project.Songs.Select(s => new Dictionary<string, object>
            {
                ["name"] = s.Name,
                ["length"] = s.Length,
                ["patternLength"] = s.PatternLength,
                ["bpm"] = s.BPM,
                ["channels"] = s.Channels.Length,
            }).ToList();

            var instruments = project.Instruments.Select(i => new Dictionary<string, object>
            {
                ["name"] = i.Name,
                ["expansion"] = ExpansionType.InternalNames[i.Expansion],
            }).ToList();

            return new Dictionary<string, object>
            {
                ["name"] = project.Name,
                ["author"] = project.Author,
                ["copyright"] = project.Copyright,
                ["songs"] = songs,
                ["instruments"] = instruments,
            };
        }

        private static string SerializeProjectText(Project project)
        {
            if (project == null) throw new Exception("No project loaded.");
            var tmp = Path.Combine(Path.GetTempPath(), $"fs_mcp_{Guid.NewGuid():N}.txt");
            try
            {
                var ids = project.Songs.Select(s => s.Id).ToArray();
                if (!new FamistudioTextFile().Save(project, tmp, ids, false))
                    throw new Exception("Failed to serialize project to text.");
                return File.ReadAllText(tmp);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static Project LoadProjectFromText(string text)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"fs_mcp_{Guid.NewGuid():N}.txt");
            try
            {
                File.WriteAllText(tmp, text);
                var project = new FamistudioTextFile().Load(tmp);
                if (project == null)
                    throw new Exception("Failed to parse FamiStudio text. Check the format and version header.");
                return project;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static void SaveProject(Project project, string path)
        {
            if (project == null) throw new Exception("No project loaded.");

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".txt")
            {
                var ids = project.Songs.Select(s => s.Id).ToArray();
                if (!new FamistudioTextFile().Save(project, path, ids, false))
                    throw new Exception("Failed to save FamiStudio text file.");
            }
            else // default to native .fms
            {
                if (!new ProjectFile().Save(project, path))
                    throw new Exception("Failed to save FamiStudio project file.");
            }
        }

        private static int GetInt(Dictionary<string, JsonElement> args, string key, int def)
        {
            if (args != null && args.TryGetValue(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j)) return j;
            }
            return def;
        }

        private static string GetString(Dictionary<string, JsonElement> args, string key, string def)
        {
            if (args != null && args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return def;
        }
    }
}
