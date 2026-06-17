using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FamiStudio
{
    // High-level ("semantic") authoring operations over an in-memory Project. Pure with respect to
    // transport: it knows nothing about HTTP or MCP. The same engine backs both the live control
    // server (mutating the running project) and offline file authoring (via the CLI subcommand).
    //
    // All operations work through FamiStudio's own model APIs, so validity and round-tripping are
    // guaranteed by the model itself. Methods return small Dictionary results that serialize to JSON.
    public static class SemanticEngine
    {
        // Set of semantic command names this engine handles (used by callers to route).
        public static readonly HashSet<string> Commands = new()
        {
            "list_channels", "list_instruments", "add_melody", "clear_channel",
            "set_tempo", "set_famistudio_tempo", "set_famitracker_tempo", "add_instrument",
        };

        // Single entry point shared by the live control server and the offline CLI subcommand.
        // Parses JSON args and dispatches to the typed methods below.
        public static Dictionary<string, object> Apply(Project project, string op, Dictionary<string, JsonElement> args)
        {
            args ??= new Dictionary<string, JsonElement>();
            switch (op)
            {
                case "list_channels":
                    return ListChannels(project, ArgInt(args, "song", 0));
                case "list_instruments":
                    return ListInstruments(project);
                case "add_melody":
                    return AddMelody(project,
                        ArgString(args, "notes", null),
                        ArgString(args, "channel", null),
                        ArgInt(args, "song", 0),
                        ArgInt(args, "startRow", 0),
                        ArgString(args, "instrument", null),
                        ArgBool(args, "replace", false));
                case "clear_channel":
                    return ClearChannel(project,
                        ArgString(args, "channel", null),
                        ArgInt(args, "song", 0),
                        ArgInt(args, "fromRow", 0),
                        ArgInt(args, "toRow", int.MaxValue));
                case "set_tempo":
                    return SetTempo(project, ArgFloat(args, "bpm", 120f), ArgInt(args, "song", 0), ArgOptInt(args, "notesPerBeat"));
                case "set_famistudio_tempo":
                    return SetFamiStudioTempo(project, ArgIntArray(args, "groove"), ArgInt(args, "song", 0), ArgOptInt(args, "notesPerBeat"));
                case "set_famitracker_tempo":
                    return SetFamiTrackerTempo(project, ArgInt(args, "speed", 6), ArgInt(args, "tempo", 150), ArgInt(args, "song", 0));
                case "add_instrument":
                    return AddInstrument(project,
                        ArgString(args, "name", null),
                        ArgString(args, "expansion", "none"),
                        ArgString(args, "preset", null),
                        args.ContainsKey("volumeEnvelope") ? ArgIntArray(args, "volumeEnvelope") : null,
                        args.ContainsKey("dutyEnvelope") ? ArgIntArray(args, "dutyEnvelope") : null);
                default:
                    throw new Exception($"Unknown semantic op: '{op}'.");
            }
        }

        // -----------------------------------------------------------------------------------
        // Melody
        // -----------------------------------------------------------------------------------

        // Note-input DSL (whitespace-separated tokens):
        //   note:dur   musical note for <dur> rows           e.g. C4:4  F#3:2  Eb5:8
        //   R:dur      rest for <dur> rows
        //   -          extend the previous musical note by 1 row (and advance 1 row)
        //   ^          stop/note-off at the current row (advance 1 row)
        public static Dictionary<string, object> AddMelody(
            Project project, string notes, string channelName, int songIndex,
            int startRow, string instrumentName, bool replace)
        {
            var song = GetSong(project, songIndex);

            var channelType = ChannelType.GetValueForInternalName(channelName);
            if (channelType < 0)
                throw new Exception($"Unknown channel '{channelName}'. Valid: {string.Join(", ", ChannelNames(song))}.");

            var channel = song.GetChannelByType(channelType)
                ?? throw new Exception($"Channel '{channelName}' is not present in this project's expansion. Valid: {string.Join(", ", ChannelNames(song))}.");

            var instrument = ResolveInstrument(project, instrumentName);

            var tokens = (notes ?? "").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                throw new Exception("No notes provided.");

            // First pass: compute the end row so we can size the song and optionally clear the range.
            var endRow = startRow;
            foreach (var t in tokens)
                endRow += TokenRows(t);

            var patternLength = song.PatternLength;
            if (patternLength <= 0)
                throw new Exception("Song has an invalid pattern length.");

            EnsureSongLength(song, (endRow + patternLength - 1) / patternLength);

            if (replace)
                ClearRange(channel, startRow, endRow, patternLength);

            // Second pass: write notes.
            var row = startRow;
            Note lastMusical = null;
            var written = 0;

            foreach (var token in tokens)
            {
                if (token == "-")
                {
                    if (lastMusical != null)
                        lastMusical.Duration += 1;
                    row += 1;
                    continue;
                }

                if (token == "^")
                {
                    var stop = GetOrCreateNote(channel, row, patternLength);
                    stop.Value = Note.NoteStop;
                    lastMusical = null;
                    row += 1;
                    continue;
                }

                var (head, dur) = SplitToken(token);

                if (head.Equals("R", StringComparison.OrdinalIgnoreCase))
                {
                    row += dur;
                    continue;
                }

                var value = Note.FromFriendlyName(head);
                if (value == Note.NoteInvalid || value < Note.MusicalNoteMin || value > Note.MusicalNoteMax)
                    throw new Exception($"Invalid note '{head}' in token '{token}'. Use names like C4, F#3, Eb5.");

                var note = GetOrCreateNote(channel, row, patternLength);
                note.Value = (byte)value;
                note.Instrument = instrument;
                note.Duration = dur;
                lastMusical = note;
                written++;
                row += dur;
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["channel"] = channelName,
                ["song"] = songIndex,
                ["notesWritten"] = written,
                ["startRow"] = startRow,
                ["endRow"] = endRow,
                ["instrument"] = instrument.Name,
            };
        }

        // -----------------------------------------------------------------------------------
        // Tempo
        // -----------------------------------------------------------------------------------

        public static Dictionary<string, object> SetTempo(Project project, float bpm, int songIndex, int? notesPerBeat)
        {
            var song = GetSong(project, songIndex);

            if (song.UsesFamiTrackerTempo)
            {
                // Approximate: keep speed, solve tempo so that BPM = tempo * 6 / (speed * notesPerRow...).
                // FamiTracker BPM = (tempo * 6) / (speed * rowsPerBeat). Use beatLength as rows/beat.
                var rowsPerBeat = Math.Max(1, song.BeatLength);
                var speed = Math.Max(1, song.FamitrackerSpeed);
                var tempo = (int)Math.Round(bpm * speed * rowsPerBeat / 6.0);
                tempo = Math.Clamp(tempo, 1, 255);
                song.FamitrackerTempo = tempo;
                var achieved = 6.0 * tempo / (speed * rowsPerBeat);
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["mode"] = "famitracker", ["requestedBpm"] = bpm,
                    ["achievedBpm"] = (float)achieved, ["speed"] = speed, ["tempo"] = tempo,
                };
            }

            var npb = notesPerBeat ?? Math.Max(1, song.BeatLength / Math.Max(1, song.NoteLength));
            npb = Math.Max(1, npb);

            var tempos = FamiStudioTempoUtils.GetAvailableTempos(project.PalMode, npb);
            if (tempos == null || tempos.Length == 0)
                throw new Exception("No available tempos for this configuration.");

            TempoInfo best = tempos[0];
            var bestErr = float.MaxValue;
            foreach (var ti in tempos)
            {
                var b = FamiStudioTempoUtils.ComputeBpmForGroove(project.PalMode, ti.groove, npb);
                var err = Math.Abs(b - bpm);
                if (err < bestErr) { bestErr = err; best = ti; }
            }

            ApplyFamiStudioGroove(song, best.groove, npb);
            var achievedBpm = FamiStudioTempoUtils.ComputeBpmForGroove(project.PalMode, best.groove, npb);

            return new Dictionary<string, object>
            {
                ["ok"] = true, ["mode"] = "famistudio", ["requestedBpm"] = bpm,
                ["achievedBpm"] = achievedBpm, ["groove"] = best.groove, ["notesPerBeat"] = npb,
            };
        }

        public static Dictionary<string, object> SetFamiStudioTempo(Project project, int[] groove, int songIndex, int? notesPerBeat)
        {
            var song = GetSong(project, songIndex);
            if (song.UsesFamiTrackerTempo)
                throw new Exception("This song uses FamiTracker tempo; use set_famitracker_tempo instead.");
            if (groove == null || groove.Length == 0 || !FamiStudioTempoUtils.ValidateGroove(groove))
                throw new Exception("Invalid groove. Each entry is a frame count; see the FamiStudio tempo docs.");

            var npb = notesPerBeat ?? Math.Max(1, song.BeatLength / Math.Max(1, song.NoteLength));
            ApplyFamiStudioGroove(song, groove, Math.Max(1, npb));
            var achievedBpm = FamiStudioTempoUtils.ComputeBpmForGroove(project.PalMode, groove, Math.Max(1, npb));

            return new Dictionary<string, object>
            {
                ["ok"] = true, ["mode"] = "famistudio", ["achievedBpm"] = achievedBpm, ["groove"] = groove,
            };
        }

        public static Dictionary<string, object> SetFamiTrackerTempo(Project project, int speed, int tempo, int songIndex)
        {
            var song = GetSong(project, songIndex);
            if (!song.UsesFamiTrackerTempo)
                throw new Exception("This song uses FamiStudio tempo; use set_tempo or set_famistudio_tempo instead.");

            song.FamitrackerSpeed = Math.Clamp(speed, 1, 31);
            song.FamitrackerTempo = Math.Clamp(tempo, 1, 255);

            return new Dictionary<string, object>
            {
                ["ok"] = true, ["mode"] = "famitracker",
                ["speed"] = song.FamitrackerSpeed, ["tempo"] = song.FamitrackerTempo,
            };
        }

        // -----------------------------------------------------------------------------------
        // Instruments
        // -----------------------------------------------------------------------------------

        private static readonly Dictionary<string, (sbyte[] vol, sbyte[] duty)> Presets = new()
        {
            ["lead"]  = (new sbyte[] { 15, 15, 14, 12, 10, 8, 6, 4 }, new sbyte[] { 2 }),
            ["bass"]  = (new sbyte[] { 15, 15, 13, 11, 9, 7 },        new sbyte[] { 0 }),
            ["pad"]   = (new sbyte[] { 8, 10, 12, 12, 10, 8 },        new sbyte[] { 2 }),
            ["pluck"] = (new sbyte[] { 15, 10, 6, 3, 1 },             new sbyte[] { 1 }),
            ["blip"]  = (new sbyte[] { 15, 8, 2 },                    new sbyte[] { 2 }),
        };

        public static Dictionary<string, object> AddInstrument(
            Project project, string name, string expansionName, string preset,
            int[] volumeEnvelope, int[] dutyEnvelope)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new Exception("Instrument name is required.");

            var expansion = ResolveExpansion(expansionName);
            var instrument = project.CreateInstrument(expansion, name)
                ?? throw new Exception($"Could not create instrument (expansion '{expansionName}' may not be enabled in the project).");

            sbyte[] vol = null, duty = null;

            if (!string.IsNullOrEmpty(preset))
            {
                if (!Presets.TryGetValue(preset.ToLowerInvariant(), out var p))
                    throw new Exception($"Unknown preset '{preset}'. Valid: {string.Join(", ", Presets.Keys)}.");
                vol = p.vol; duty = p.duty;
            }

            if (volumeEnvelope != null) vol = volumeEnvelope.Select(v => (sbyte)v).ToArray();
            if (dutyEnvelope != null) duty = dutyEnvelope.Select(v => (sbyte)v).ToArray();

            if (vol != null) SetEnvelope(instrument, EnvelopeType.Volume, vol);
            if (duty != null) SetEnvelope(instrument, EnvelopeType.DutyCycle, duty);

            return new Dictionary<string, object>
            {
                ["ok"] = true, ["name"] = instrument.Name,
                ["expansion"] = ExpansionType.InternalNames[expansion],
                ["preset"] = preset,
                ["volumeEnvelope"] = vol?.Select(v => (int)v).ToArray(),
                ["dutyEnvelope"] = duty?.Select(v => (int)v).ToArray(),
            };
        }

        // -----------------------------------------------------------------------------------
        // Inspect / clear
        // -----------------------------------------------------------------------------------

        public static Dictionary<string, object> ListChannels(Project project, int songIndex)
        {
            var song = GetSong(project, songIndex);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["channels"] = song.Channels.Select(c => new Dictionary<string, object>
                {
                    ["name"] = c.Name,
                    ["type"] = c.Type,
                }).ToList(),
            };
        }

        public static Dictionary<string, object> ListInstruments(Project project)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["instruments"] = project.Instruments.Select(i => new Dictionary<string, object>
                {
                    ["name"] = i.Name,
                    ["expansion"] = ExpansionType.InternalNames[i.Expansion],
                }).ToList(),
            };
        }

        public static Dictionary<string, object> ClearChannel(Project project, string channelName, int songIndex, int fromRow, int toRow)
        {
            var song = GetSong(project, songIndex);
            var channelType = ChannelType.GetValueForInternalName(channelName);
            if (channelType < 0)
                throw new Exception($"Unknown channel '{channelName}'. Valid: {string.Join(", ", ChannelNames(song))}.");
            var channel = song.GetChannelByType(channelType)
                ?? throw new Exception($"Channel '{channelName}' is not present in this project's expansion.");

            ClearRange(channel, fromRow, toRow, song.PatternLength);
            return new Dictionary<string, object> { ["ok"] = true, ["channel"] = channelName, ["fromRow"] = fromRow, ["toRow"] = toRow };
        }

        // -----------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------

        private static Song GetSong(Project project, int index)
        {
            if (project == null) throw new Exception("No project loaded.");
            if (index < 0 || index >= project.Songs.Count)
                throw new Exception($"Song index {index} out of range (0..{project.Songs.Count - 1}).");
            return project.Songs[index];
        }

        private static IEnumerable<string> ChannelNames(Song song) => song.Channels.Select(c => c.Name);

        private static Instrument ResolveInstrument(Project project, string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                var found = project.Instruments.FirstOrDefault(i => i.Name == name);
                return found ?? throw new Exception($"Instrument '{name}' not found.");
            }
            if (project.Instruments.Count == 0)
                throw new Exception("Project has no instruments; create one with add_instrument first.");
            return project.Instruments[0];
        }

        private static int ResolveExpansion(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Equals("none", StringComparison.OrdinalIgnoreCase))
                return ExpansionType.None;
            var idx = Array.FindIndex(ExpansionType.InternalNames, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                throw new Exception($"Unknown expansion '{name}'. Valid: none, {string.Join(", ", ExpansionType.InternalNames.Skip(1))}.");
            return idx;
        }

        private static int TokenRows(string token)
        {
            if (token == "-" || token == "^") return 1;
            var (_, dur) = SplitToken(token);
            return dur;
        }

        private static (string head, int dur) SplitToken(string token)
        {
            var colon = token.IndexOf(':');
            if (colon < 0)
                throw new Exception($"Token '{token}' is missing a duration (use note:duration, e.g. C4:4).");
            var head = token.Substring(0, colon);
            if (!int.TryParse(token.Substring(colon + 1), out var dur) || dur <= 0)
                throw new Exception($"Token '{token}' has an invalid duration.");
            return (head, dur);
        }

        private static void EnsureSongLength(Song song, int neededPatterns)
        {
            if (neededPatterns > song.Length)
                song.SetLength(Math.Min(neededPatterns, Song.MaxLength));
        }

        private static Note GetOrCreateNote(Channel channel, int absoluteRow, int patternLength)
        {
            var patternIdx = absoluteRow / patternLength;
            var timeInPattern = absoluteRow % patternLength;

            var pattern = channel.PatternInstances[patternIdx];
            if (pattern == null)
            {
                pattern = channel.CreatePattern();
                channel.PatternInstances[patternIdx] = pattern;
            }
            return pattern.GetOrCreateNoteAt(timeInPattern);
        }

        private static void ClearRange(Channel channel, int fromRow, int toRow, int patternLength)
        {
            for (var row = fromRow; row < toRow; row++)
            {
                var patternIdx = row / patternLength;
                if (patternIdx >= channel.PatternInstances.Length) break;
                var pattern = channel.PatternInstances[patternIdx];
                pattern?.Notes.Remove(row % patternLength);
            }
        }

        private static void ApplyFamiStudioGroove(Song song, int[] groove, int notesPerBeat)
        {
            var noteLength = groove.Min();
            // Mirror the Tempo dialog: pick a sensible pattern/beat length for the new note length.
            song.ChangeFamiStudioTempoGroove(groove, true);
            song.SetBeatLength(noteLength * notesPerBeat);
            song.SetSensibleBeatLength();
        }

        private static void SetEnvelope(Instrument instrument, int envelopeType, sbyte[] values)
        {
            var env = instrument.Envelopes[envelopeType];
            if (env == null)
                throw new Exception($"Instrument does not support envelope type {envelopeType}.");

            var max = env.MaxLength;
            if (values.Length > max)
                throw new Exception($"Envelope too long ({values.Length}); max is {max} for this type.");

            env.Length = values.Length;
            for (var i = 0; i < values.Length; i++)
                env.Values[i] = values[i];
        }

        // --- JSON argument extraction (shared by Apply's callers) ---

        private static int ArgInt(Dictionary<string, JsonElement> args, string key, int def)
        {
            if (args.TryGetValue(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j)) return j;
            }
            return def;
        }

        private static int? ArgOptInt(Dictionary<string, JsonElement> args, string key)
            => args.ContainsKey(key) ? ArgInt(args, key, 0) : (int?)null;

        private static float ArgFloat(Dictionary<string, JsonElement> args, string key, float def)
        {
            if (args.TryGetValue(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (float)d;
                if (v.ValueKind == JsonValueKind.String && float.TryParse(v.GetString(), out var f)) return f;
            }
            return def;
        }

        private static bool ArgBool(Dictionary<string, JsonElement> args, string key, bool def)
        {
            if (args.TryGetValue(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
                if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
            }
            return def;
        }

        private static string ArgString(Dictionary<string, JsonElement> args, string key, string def)
        {
            if (args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return def;
        }

        private static int[] ArgIntArray(Dictionary<string, JsonElement> args, string key)
        {
            if (!args.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array)
                throw new Exception($"Missing or invalid array '{key}'.");
            var list = new List<int>();
            foreach (var e in v.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i)) list.Add(i);
                else if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var j)) list.Add(j);
                else throw new Exception($"Array '{key}' must contain integers.");
            }
            return list.ToArray();
        }
    }
}
